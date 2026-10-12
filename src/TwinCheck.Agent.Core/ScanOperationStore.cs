using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

[assembly: InternalsVisibleTo("TwinCheck.Agent.Tests")]

namespace TwinCheck.Agent.Core;

public sealed class ScanOperationHistoryExpiredException(string idempotencyKey, string status, DateTimeOffset completedAt)
    : InvalidOperationException($"History for scan operation '{idempotencyKey}' has expired; its key is still retained to prevent duplicate processing.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
    public string Status { get; } = status;
    public DateTimeOffset CompletedAt { get; } = completedAt;
}

public sealed record JobCleanupResult(int HistoryDeleted, int KeysDeleted);

public sealed class ScanOperationStore
{
    public const string StateDirectoryEnvironmentVariable = "TWINCHECK_AGENT_STATE_DIR";
    public const int HistoryRetentionDays = 30;
    public const int KeyRetentionDays = 180;

    // Finished jobs and retained keys are read from SQLite on demand, never cached.
    private readonly Dictionary<string, ScanOperationState> active = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    // Preserve the original JSON store's fingerprint format for migrated requests.
    private readonly JsonSerializerOptions fingerprintOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly LocalAgentLogger? logger;
    private readonly string connectionString;

    public ScanOperationStore(LocalAgentLogger logger) : this(DefaultStateDirectory, logger) { }

    public ScanOperationStore(string stateDirectory, LocalAgentLogger? logger = null)
    {
        StateDirectory = Path.GetFullPath(stateDirectory);
        DatabasePath = Path.Combine(StateDirectory, "jobs.sqlite3");
        this.logger = logger;
        Directory.CreateDirectory(StateDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            // Connections are short-lived so upgrades/backups don't leave pooled file handles open.
            Pooling = false
        }.ToString();
        Initialize();
        ImportLegacyJobs();
        Cleanup();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT j.*, k.fingerprint FROM jobs j JOIN job_keys k USING (idempotency_key) WHERE j.is_terminal = 0";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var operation = ReadOperation(reader);
            active.Add(operation.IdempotencyKey, operation);
        }
    }

    public static string DefaultStateDirectory
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable(StateDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(configuredPath);
            return Directory.GetParent(LocalAgentLogger.DefaultLogDirectory)?.FullName
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TwinCheck", "ScanAgent");
        }
    }

    public string StateDirectory { get; }
    public string DatabasePath { get; }

    internal int CachedActiveCount { get { lock (gate) return active.Count; } }

    public EnqueueScanOperationResult Enqueue(ProcessScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));

        // Canonicalize through typed DTOs: property order is fixed by the schema, not incoming JSON/JSONB order.
        // Keep the legacy indented representation; Metadata is omitted when null to preserve old fingerprints.
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, fingerprintOptions)))).ToLowerInvariant();
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT fingerprint, status, created_at, completed_at FROM job_keys WHERE idempotency_key = $key";
            Add(lookup, "$key", request.IdempotencyKey);
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                var existingFingerprint = reader.GetString(0);
                var conflict = !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal);
                var status = reader.GetString(1);
                var createdAt = ParseDate(reader.GetString(2));
                var completedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : ParseDate(reader.GetString(3));
                reader.Close();
                var existing = ReadJob(connection, request.IdempotencyKey, transaction);
                if (existing is null)
                {
                    if (!conflict) throw new ScanOperationHistoryExpiredException(request.IdempotencyKey, status, completedAt!.Value);
                    existing = new ScanOperationState
                    {
                        OperationId = request.IdempotencyKey, IdempotencyKey = request.IdempotencyKey,
                        RequestFingerprint = existingFingerprint, Request = request, Status = status,
                        CreatedAt = createdAt, CompletedAt = completedAt
                    };
                }
                return new EnqueueScanOperationResult(existing, Created: false, Conflict: conflict);
            }
            reader.Close();
            var operation = new ScanOperationState
            {
                OperationId = request.IdempotencyKey, IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = fingerprint, Request = request, Message = "Scan operation queued."
            };
            Insert(connection, transaction, operation);
            transaction.Commit();
            active[operation.IdempotencyKey] = operation;
            return new EnqueueScanOperationResult(operation, Created: true, Conflict: false);
        }
    }

    public ScanOperationState Get(string idempotencyKey)
    {
        lock (gate)
        {
            if (active.TryGetValue(idempotencyKey, out var operation)) return operation;
            using var connection = Open();
            operation = ReadJob(connection, idempotencyKey);
            if (operation is not null) return operation;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, completed_at FROM job_keys WHERE idempotency_key = $key";
            Add(command, "$key", idempotencyKey);
            using var reader = command.ExecuteReader();
            if (reader.Read()) throw new ScanOperationHistoryExpiredException(idempotencyKey, reader.GetString(0), ParseDate(reader.GetString(1)));
            throw new KeyNotFoundException($"Unknown scan operation '{idempotencyKey}'.");
        }
    }

    public IReadOnlyList<ScanOperationState> ListActive()
    {
        lock (gate) return active.Values.OrderByDescending(operation => operation.CreatedAt).ToArray();
    }

    public IReadOnlyList<ScanJobSummary> ListRecent(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Both branches use the (is_terminal, created_at) index. No full-history sort or payload parsing.
        command.CommandText = """
            SELECT * FROM (
                SELECT idempotency_key, profile_id, order_number, roll_number, status, phase, created_at,
                       completed_at, message, error, candidate_path, resolved_source_dir, destination_dir,
                       files_completed, file_count, is_terminal
                FROM jobs WHERE is_terminal = 0
                UNION ALL
                SELECT * FROM (
                    SELECT idempotency_key, profile_id, order_number, roll_number, status, phase, created_at,
                           completed_at, message, error, candidate_path, resolved_source_dir, destination_dir,
                           files_completed, file_count, is_terminal
                    FROM jobs WHERE is_terminal = 1 ORDER BY created_at DESC LIMIT $limit
                )
            ) ORDER BY is_terminal, created_at DESC
            """;
        Add(command, "$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var summaries = new List<ScanJobSummary>();
        while (reader.Read()) summaries.Add(new ScanJobSummary(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), ParseDate(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
            String(reader, 8), String(reader, 9), String(reader, 10), String(reader, 11), String(reader, 12),
            reader.GetInt32(13), reader.GetInt32(14)));
        return summaries;
    }

    public ScanOperationState Update(string idempotencyKey, Func<ScanOperationState, ScanOperationState> update)
    {
        lock (gate)
        {
            var current = Get(idempotencyKey);
            var next = update(current);
            if (next.IdempotencyKey != current.IdempotencyKey || next.RequestFingerprint != current.RequestFingerprint
                || next.Request != current.Request)
                throw new InvalidOperationException("A scan operation's key and request cannot change after enqueue.");
            if (ScanOperationStatuses.IsTerminal(next.Status) && next.CompletedAt is null)
                next = next with { CompletedAt = DateTimeOffset.UtcNow };
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE jobs SET status=$status, phase=$phase, is_terminal=$terminal,
                    files_completed=$files, file_count=$count, bytes_completed=$bytes, total_bytes=$total,
                    started_at=$started, completed_at=$completed, message=$message, error=$error,
                    error_code=$code, error_source_dir=$errorSource, resolved_source_dir=$source,
                    candidate_path=$candidatePath, destination_dir=$destination
                """;
            // Progress updates never serialize/rewrite the immutable request or completed result.
            if (next.Candidate != current.Candidate)
            {
                command.CommandText += ", candidate_json=$candidate";
                Add(command, "$candidate", Serialize(next.Candidate));
            }
            if (next.Result != current.Result)
            {
                command.CommandText += ", result_json=$result";
                Add(command, "$result", Serialize(next.Result));
            }
            command.CommandText += " WHERE idempotency_key=$key";
            AddState(command, next);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"Unknown scan operation '{idempotencyKey}'.");
            using var keyUpdate = connection.CreateCommand();
            keyUpdate.Transaction = transaction;
            keyUpdate.CommandText = "UPDATE job_keys SET status=$status, completed_at=$completed WHERE idempotency_key=$key";
            Add(keyUpdate, "$key", idempotencyKey);
            Add(keyUpdate, "$status", next.Status);
            Add(keyUpdate, "$completed", Date(next.CompletedAt));
            keyUpdate.ExecuteNonQuery();
            transaction.Commit();
            if (ScanOperationStatuses.IsTerminal(next.Status)) active.Remove(idempotencyKey);
            else active[idempotencyKey] = next;
            return next;
        }
    }

    public JobCleanupResult Cleanup(DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var timestamp = now ?? DateTimeOffset.UtcNow;
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM jobs WHERE is_terminal=1 AND completed_at < $cutoff";
            Add(command, "$cutoff", Date(timestamp.AddDays(-HistoryRetentionDays)));
            var historyDeleted = command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM job_keys WHERE completed_at < $cutoff AND NOT EXISTS (SELECT 1 FROM jobs WHERE jobs.idempotency_key=job_keys.idempotency_key)";
            Add(command, "$cutoff", Date(timestamp.AddDays(-KeyRetentionDays)));
            var keysDeleted = command.ExecuteNonQuery();
            transaction.Commit();
            using var vacuum = connection.CreateCommand();
            // Reclaim a bounded number of free pages each pass, without a blocking full VACUUM.
            vacuum.CommandText = "PRAGMA incremental_vacuum(256)";
            vacuum.ExecuteNonQuery();
            if (historyDeleted > 0 || keysDeleted > 0)
                logger?.Info($"Cleaned job history: {historyDeleted} job(s), {keysDeleted} retained key(s).");
            return new JobCleanupResult(historyDeleted, keysDeleted);
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = (long)command.ExecuteScalar()!;
        if (version > 1) throw new InvalidOperationException($"Job database schema {version} is newer than this agent supports.");
        command.CommandText = """
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS job_keys (
                idempotency_key TEXT PRIMARY KEY,
                fingerprint TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_job_keys_completed ON job_keys(completed_at);
            CREATE TABLE IF NOT EXISTS jobs (
                idempotency_key TEXT PRIMARY KEY REFERENCES job_keys(idempotency_key),
                operation_id TEXT NOT NULL,
                request_json TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                order_number TEXT NOT NULL,
                roll_number TEXT NOT NULL,
                status TEXT NOT NULL,
                phase TEXT NOT NULL,
                is_terminal INTEGER NOT NULL,
                files_completed INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                bytes_completed INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                message TEXT,
                error TEXT,
                error_code TEXT,
                error_source_dir TEXT,
                resolved_source_dir TEXT,
                candidate_path TEXT,
                destination_dir TEXT,
                candidate_json TEXT,
                result_json TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_jobs_recent ON jobs(is_terminal, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_jobs_cleanup ON jobs(is_terminal, completed_at);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private void ImportLegacyJobs()
    {
        var directory = Path.Combine(StateDirectory, "operations");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "operation-*.json"))
        {
            ScanOperationState? operation;
            try
            {
                operation = JsonSerializer.Deserialize<ScanOperationState>(File.ReadAllText(path), jsonOptions);
                if (operation is null || string.IsNullOrWhiteSpace(operation.IdempotencyKey))
                    throw new JsonException("Missing operation or job key.");
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                logger?.Warning($"Skipped unreadable legacy scan operation '{path}': {exception.Message}");
                continue;
            }
            if (ScanOperationStatuses.IsTerminal(operation.Status) && operation.CompletedAt is null)
                operation = operation with { CompletedAt = operation.CreatedAt };
            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using var check = connection.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = "SELECT fingerprint FROM job_keys WHERE idempotency_key=$key";
                Add(check, "$key", operation.IdempotencyKey);
                var existingFingerprint = check.ExecuteScalar() as string;
                if (existingFingerprint is null) Insert(connection, transaction, operation);
                else if (existingFingerprint != operation.RequestFingerprint)
                    throw new InvalidOperationException($"Legacy job '{path}' conflicts with an existing database job; the source file was preserved.");
                transaction.Commit();
            }
            // Only retire the source file after the database commit. An interrupted import is safe to retry.
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fail startup rather than resume workflows with a stale legacy file that can later resurrect.
                throw new IOException($"Imported legacy job but could not retire '{path}'. Check state directory permissions.", exception);
            }
            logger?.Info($"Migrated scan operation {operation.IdempotencyKey} to SQLite.");
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private void Insert(SqliteConnection connection, SqliteTransaction transaction, ScanOperationState operation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_keys VALUES ($key, $fingerprint, $status, $created, $completed);
            INSERT INTO jobs (
                idempotency_key, operation_id, request_json, profile_id, order_number, roll_number,
                status, phase, is_terminal, files_completed, file_count, bytes_completed, total_bytes,
                created_at, started_at, completed_at, message, error, error_code, error_source_dir,
                resolved_source_dir, candidate_path, destination_dir, candidate_json, result_json
            ) VALUES (
                $key, $operation, $request, $profile, $order, $roll, $status, $phase, $terminal,
                $files, $count, $bytes, $total, $created, $started, $completed, $message, $error,
                $code, $errorSource, $source, $candidatePath, $destination, $candidate, $result
            );
            """;
        AddState(command, operation);
        Add(command, "$fingerprint", operation.RequestFingerprint);
        Add(command, "$created", Date(operation.CreatedAt));
        Add(command, "$operation", operation.OperationId);
        Add(command, "$request", Serialize(operation.Request));
        Add(command, "$profile", operation.Request.ProfileId);
        Add(command, "$order", operation.Request.OrderNumber);
        Add(command, "$roll", operation.Request.RollNumber);
        Add(command, "$candidate", Serialize(operation.Candidate));
        Add(command, "$result", Serialize(operation.Result));
        command.ExecuteNonQuery();
    }

    private static void AddState(SqliteCommand command, ScanOperationState operation)
    {
        Add(command, "$key", operation.IdempotencyKey);
        Add(command, "$status", operation.Status);
        Add(command, "$phase", operation.Phase);
        Add(command, "$terminal", ScanOperationStatuses.IsTerminal(operation.Status) ? 1 : 0);
        Add(command, "$files", operation.FilesCompleted);
        Add(command, "$count", operation.FileCount);
        Add(command, "$bytes", operation.BytesCompleted);
        Add(command, "$total", operation.TotalBytes);
        Add(command, "$started", Date(operation.StartedAt));
        Add(command, "$completed", Date(operation.CompletedAt));
        Add(command, "$message", operation.Message);
        Add(command, "$error", operation.Error);
        Add(command, "$code", operation.ErrorCode);
        Add(command, "$errorSource", operation.ErrorSourceDir);
        Add(command, "$source", operation.ResolvedSourceDir);
        Add(command, "$candidatePath", operation.Candidate?.Path);
        Add(command, "$destination", operation.Result?.Manifest.FinalDir ?? operation.Candidate?.DestinationPreview);
    }

    private ScanOperationState? ReadJob(SqliteConnection connection, string key, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT j.*, k.fingerprint FROM jobs j JOIN job_keys k USING (idempotency_key) WHERE j.idempotency_key=$key";
        Add(command, "$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOperation(reader) : null;
    }

    private ScanOperationState ReadOperation(SqliteDataReader reader)
    {
        string? Read(string name) => String(reader, reader.GetOrdinal(name));
        DateTimeOffset? ReadDate(string name) => Read(name) is { } value ? ParseDate(value) : null;
        return new ScanOperationState
        {
            OperationId = Read("operation_id")!, IdempotencyKey = Read("idempotency_key")!,
            RequestFingerprint = Read("fingerprint")!,
            Request = JsonSerializer.Deserialize<ProcessScanRequest>(Read("request_json")!, jsonOptions)!,
            Status = Read("status")!, Phase = Read("phase")!,
            FilesCompleted = reader.GetInt32(reader.GetOrdinal("files_completed")),
            FileCount = reader.GetInt32(reader.GetOrdinal("file_count")),
            BytesCompleted = reader.GetInt64(reader.GetOrdinal("bytes_completed")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            CreatedAt = ReadDate("created_at")!.Value, StartedAt = ReadDate("started_at"), CompletedAt = ReadDate("completed_at"),
            Message = Read("message"), Error = Read("error"), ErrorCode = Read("error_code"),
            ErrorSourceDir = Read("error_source_dir"), ResolvedSourceDir = Read("resolved_source_dir"),
            Candidate = Read("candidate_json") is { } candidate ? JsonSerializer.Deserialize<SourceCandidate>(candidate, jsonOptions) : null,
            Result = Read("result_json") is { } result ? JsonSerializer.Deserialize<ProcessScanResult>(result, jsonOptions) : null
        };
    }

    private string? Serialize<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value, jsonOptions);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? String(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string? Date(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
