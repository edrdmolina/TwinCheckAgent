using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwinCheck.Agent.Core;

public static class ScanOperationStatuses
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static bool IsTerminal(string status) => status is Completed or Failed;
}

public static class ScanOperationPhases
{
    public const string Queued = "queued";
    public const string Watching = "watching";
    public const string Settling = "settling";
    public const string Hashing = "hashing";
    public const string Copying = "copying";
    public const string Verifying = "verifying";
    public const string Finalizing = "finalizing";
    public const string Archiving = "archiving";
    public const string Complete = "complete";
    public const string Failed = "failed";
}

public sealed record ScanProgress(
    string Phase,
    int FilesCompleted,
    int FileCount,
    long BytesCompleted,
    long TotalBytes,
    string? Message = null);

public sealed record ScanOperationState
{
    public required string OperationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string RequestFingerprint { get; init; }
    public required ProcessScanRequest Request { get; init; }
    public string Status { get; init; } = ScanOperationStatuses.Queued;
    public string Phase { get; init; } = ScanOperationPhases.Queued;
    public int FilesCompleted { get; init; }
    public int FileCount { get; init; }
    public long BytesCompleted { get; init; }
    public long TotalBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorSourceDir { get; init; }
    public string? ResolvedSourceDir { get; init; }
    public SourceCandidate? Candidate { get; init; }
    public ProcessScanResult? Result { get; init; }

    public ScanOperationView ToView() =>
        new(
            OperationId,
            IdempotencyKey,
            Status,
            Phase,
            FilesCompleted,
            FileCount,
            BytesCompleted,
            TotalBytes,
            CreatedAt,
            StartedAt,
            CompletedAt,
            Message,
            Error,
            ErrorCode,
            ErrorSourceDir,
            ResolvedSourceDir,
            Candidate,
            Result);
}

public sealed record ScanOperationView(
    string OperationId,
    string IdempotencyKey,
    string Status,
    string Phase,
    int FilesCompleted,
    int FileCount,
    long BytesCompleted,
    long TotalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message,
    string? Error,
    string? ErrorCode,
    string? ErrorSourceDir,
    string? ResolvedSourceDir,
    SourceCandidate? Candidate,
    ProcessScanResult? Result);

public sealed record EnqueueScanOperationResult(ScanOperationState Operation, bool Created, bool Conflict);

public sealed class ScanOperationStore
{
    public const string StateDirectoryEnvironmentVariable = "TWINCHECK_AGENT_STATE_DIR";

    private readonly ConcurrentDictionary<string, ScanOperationState> operations = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly LocalAgentLogger? logger;

    public ScanOperationStore(LocalAgentLogger logger)
        : this(DefaultStateDirectory, logger)
    {
    }

    public ScanOperationStore(string stateDirectory, LocalAgentLogger? logger = null)
    {
        StateDirectory = Path.GetFullPath(stateDirectory);
        OperationsDirectory = Path.Combine(StateDirectory, "operations");
        this.logger = logger;
        LoadExisting();
    }

    public static string DefaultStateDirectory
    {
        get
        {
            var configuredPath = Environment.GetEnvironmentVariable(StateDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            return Directory.GetParent(LocalAgentLogger.DefaultLogDirectory)?.FullName
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TwinCheck", "ScanAgent");
        }
    }

    public string StateDirectory { get; }
    public string OperationsDirectory { get; }

    public EnqueueScanOperationResult Enqueue(ProcessScanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        }

        var fingerprint = Fingerprint(request);
        lock (gate)
        {
            if (operations.TryGetValue(request.IdempotencyKey, out var existing))
            {
                return new EnqueueScanOperationResult(
                    existing,
                    Created: false,
                    Conflict: !string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal));
            }

            var operation = new ScanOperationState
            {
                OperationId = request.IdempotencyKey,
                IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = fingerprint,
                Request = request,
                Message = "Scan operation queued."
            };
            operations[operation.IdempotencyKey] = operation;
            Write(operation);
            return new EnqueueScanOperationResult(operation, Created: true, Conflict: false);
        }
    }

    public ScanOperationState Get(string idempotencyKey) =>
        operations.TryGetValue(idempotencyKey, out var operation)
            ? operation
            : throw new KeyNotFoundException($"Unknown scan operation '{idempotencyKey}'.");

    public IReadOnlyList<ScanOperationState> List() =>
        operations.Values.OrderByDescending(operation => operation.CreatedAt).ToArray();

    public ScanOperationState Update(string idempotencyKey, Func<ScanOperationState, ScanOperationState> update)
    {
        lock (gate)
        {
            var current = Get(idempotencyKey);
            var next = update(current);
            operations[idempotencyKey] = next;
            Write(next);
            return next;
        }
    }

    private void LoadExisting()
    {
        if (!Directory.Exists(OperationsDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(OperationsDirectory, "operation-*.json"))
        {
            try
            {
                var operation = JsonSerializer.Deserialize<ScanOperationState>(File.ReadAllText(path), jsonOptions);
                if (operation is not null && !string.IsNullOrWhiteSpace(operation.IdempotencyKey))
                {
                    operations[operation.IdempotencyKey] = operation;
                }
            }
            catch (Exception exception)
            {
                logger?.Warning($"Skipped unreadable scan operation state '{path}': {exception.Message}");
            }
        }
    }

    private void Write(ScanOperationState operation)
    {
        Directory.CreateDirectory(OperationsDirectory);
        var path = GetPath(operation.IdempotencyKey);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(operation, jsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetPath(string idempotencyKey)
    {
        var safeKey = string.Concat(idempotencyKey.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant()[..12];
        return Path.Combine(OperationsDirectory, $"operation-{safeKey}-{suffix}.json");
    }

    private string Fingerprint(ProcessScanRequest request)
    {
        var json = JsonSerializer.Serialize(request, jsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

public sealed class ScanOperationService : IDisposable
{
    private readonly ScanOperationStore store;
    private readonly ScanProcessor processor;
    private readonly ScanWatchService watchService;
    private readonly ScanReadinessCoordinator readinessCoordinator;
    private readonly LocalAgentLogger logger;
    private readonly ConcurrentDictionary<string, Task> running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> profileGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim processingGate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();

    public ScanOperationService(
        ScanOperationStore store,
        ScanProcessor processor,
        ScanWatchService watchService,
        LocalAgentLogger logger)
    {
        this.store = store;
        this.processor = processor;
        this.watchService = watchService;
        readinessCoordinator = watchService.ReadinessCoordinator;
        this.logger = logger;

        foreach (var operation in store.List().Where(operation => !ScanOperationStatuses.IsTerminal(operation.Status)))
        {
            store.Update(operation.IdempotencyKey, current => current with
            {
                Status = ScanOperationStatuses.Queued,
                Message = "Recovered after agent restart; scan workflow resumed."
            });
            Schedule(operation.IdempotencyKey);
        }
    }

    public EnqueueScanOperationResult Enqueue(ProcessScanRequest request)
    {
        var result = store.Enqueue(request);
        if (result.Created)
        {
            Schedule(result.Operation.IdempotencyKey);
        }

        return result;
    }

    public ScanOperationState Get(string idempotencyKey) => store.Get(idempotencyKey);

    public IReadOnlyList<ScanOperationView> ListActive() =>
        store.List()
            .Where(operation => !ScanOperationStatuses.IsTerminal(operation.Status))
            .Select(operation => operation.ToView())
            .ToArray();

    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            Task.WaitAll(running.Values.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch
        {
            // The persisted operation will be recovered on the next start.
        }
        cancellation.Dispose();
    }

    private void Schedule(string idempotencyKey)
    {
        running.GetOrAdd(idempotencyKey, key => Task.Run(async () =>
        {
            try
            {
                await RunWorkflow(key);
            }
            finally
            {
                running.TryRemove(key, out _);
            }
        }));
    }

    private async Task RunWorkflow(string idempotencyKey)
    {
        var operation = store.Get(idempotencyKey);
        if (ScanOperationStatuses.IsTerminal(operation.Status))
        {
            return;
        }

        var profileGate = profileGates.GetOrAdd(operation.Request.ProfileId, _ => new SemaphoreSlim(1, 1));
        await profileGate.WaitAsync(cancellation.Token);
        try
        {
            await PrepareAndProcess(operation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            store.Update(idempotencyKey, current => current with
            {
                Status = ScanOperationStatuses.Queued,
                Message = "Agent stopped; this workflow will resume after restart."
            });
        }
        finally
        {
            profileGate.Release();
        }
    }

    private async Task PrepareAndProcess(ScanOperationState originalOperation)
    {
        var idempotencyKey = originalOperation.IdempotencyKey;
        var operation = store.Get(idempotencyKey);
        var effectiveRequest = operation.Request;

        try
        {
            if (ShouldWaitForReadiness(operation))
            {
                store.Update(idempotencyKey, current => current with
                {
                    Status = ScanOperationStatuses.Processing,
                    Phase = ScanOperationPhases.Watching,
                    StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
                    Message = "Starting scanner watch."
                });
                var ready = await readinessCoordinator.WaitForReadyAsync(
                    operation.Request,
                    progress => store.Update(idempotencyKey, current => current with
                    {
                        Status = ScanOperationStatuses.Processing,
                        Phase = progress.Phase,
                        Candidate = progress.Candidate,
                        Message = progress.Message
                    }),
                    cancellation.Token);

                operation = store.Update(idempotencyKey, current => current with
                {
                    Status = ScanOperationStatuses.Queued,
                    Phase = ScanOperationPhases.Queued,
                    Candidate = ready,
                    ResolvedSourceDir = ready.Path,
                    Message = $"Scanner output ready: {ready.ImageCount} image(s)."
                });
                effectiveRequest = operation.Request with { SourceDir = ready.Path };
            }
            else if (!string.IsNullOrWhiteSpace(operation.ResolvedSourceDir))
            {
                effectiveRequest = operation.Request with { SourceDir = operation.ResolvedSourceDir };
            }

            await processingGate.WaitAsync(cancellation.Token);
            try
            {
                ProcessReadyOperation(operation, effectiveRequest);
            }
            finally
            {
                processingGate.Release();
            }
        }
        catch (ScanReadinessException exception)
        {
            var current = store.Get(idempotencyKey);
            FailOperation(current, exception.Message, exception.Code, current.Candidate?.Path);
        }
        catch (MultipleSourceCandidatesException exception)
        {
            FailOperation(operation, exception.Message, "multiple-source-candidates", exception.SourceDir);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var current = store.Get(idempotencyKey);
            FailOperation(current, exception.Message, null, current.ResolvedSourceDir ?? current.Candidate?.Path);
            logger.Error($"Scan operation {idempotencyKey} failed.", exception);
        }
    }

    private void ProcessReadyOperation(ScanOperationState operation, ProcessScanRequest effectiveRequest)
    {
        var idempotencyKey = operation.IdempotencyKey;
        var stopwatch = Stopwatch.StartNew();
        var lastPhase = store.Get(idempotencyKey).Phase;
        store.Update(idempotencyKey, current => current with
        {
            Status = ScanOperationStatuses.Processing,
            StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow,
            Message = "Scan processing started."
        });
        watchService.MarkProcessing(effectiveRequest.WatchId, idempotencyKey);
        logger.Info($"Scan operation {idempotencyKey} started.");

        var result = processor.Process(effectiveRequest, progress =>
        {
            if (!string.Equals(lastPhase, progress.Phase, StringComparison.Ordinal))
            {
                logger.Info($"Scan operation {idempotencyKey} phase {lastPhase} completed after {stopwatch.Elapsed}.");
                lastPhase = progress.Phase;
                stopwatch.Restart();
            }

            store.Update(idempotencyKey, current => current with
            {
                Status = ScanOperationStatuses.Processing,
                Phase = progress.Phase,
                FilesCompleted = progress.FilesCompleted,
                FileCount = progress.FileCount,
                BytesCompleted = progress.BytesCompleted,
                TotalBytes = progress.TotalBytes,
                Message = progress.Message
            });
        });

        var completed = store.Update(idempotencyKey, current => current with
        {
            Status = ScanOperationStatuses.Completed,
            Phase = ScanOperationPhases.Complete,
            FilesCompleted = result.Manifest.Files.Count,
            FileCount = result.Manifest.Files.Count,
            BytesCompleted = result.Manifest.Files.Sum(file => file.Size),
            TotalBytes = result.Manifest.Files.Sum(file => file.Size),
            CompletedAt = DateTimeOffset.UtcNow,
            Message = $"Completed {result.ImageCount} image(s).",
            Error = null,
            ErrorCode = null,
            ErrorSourceDir = null,
            ResolvedSourceDir = result.Manifest.SourceDir,
            Result = result
        });
        watchService.MarkCompleted(effectiveRequest.WatchId, idempotencyKey, completed.Message);
        logger.Info($"Scan operation {idempotencyKey} completed. Images: {result.ImageCount}.");
    }

    private bool ShouldWaitForReadiness(ScanOperationState operation) =>
        operation.Request.WaitForReady
        && string.IsNullOrWhiteSpace(operation.ResolvedSourceDir)
        && operation.Phase is ScanOperationPhases.Queued or ScanOperationPhases.Watching or ScanOperationPhases.Settling;

    private void FailOperation(ScanOperationState operation, string error, string? errorCode, string? errorSourceDir)
    {
        var failed = store.Update(operation.IdempotencyKey, current => current with
        {
            Status = ScanOperationStatuses.Failed,
            Phase = ScanOperationPhases.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            Message = errorCode is "watch-timeout" or "settle-timeout"
                ? "Scanner output did not become ready."
                : "Scan processing failed.",
            Error = error,
            ErrorCode = errorCode,
            ErrorSourceDir = errorSourceDir
        });
        watchService.MarkFailed(operation.Request.WatchId, operation.IdempotencyKey, failed.Error);
        logger.Warning($"Scan operation {operation.IdempotencyKey} failed{(errorCode is null ? "" : $" ({errorCode})")}: {error}");
    }
}
