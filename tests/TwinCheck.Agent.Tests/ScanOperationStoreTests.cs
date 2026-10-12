using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Tests;

public sealed class ScanOperationStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twincheck-sqlite-tests", Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void MigratesLegacyJobsAndOnlyCachesActiveOperations()
    {
        var request = Request("finished");
        var manifest = new OperationManifest
        {
            IdempotencyKey = request.IdempotencyKey, ProfileId = request.ProfileId,
            OrderNumber = request.OrderNumber, RollNumber = request.RollNumber,
            SourceDir = Path.Combine(root, "source"), DestinationDir = Path.Combine(root, "output"),
            FinalDir = Path.Combine(root, "output", "roll"), CompletedAt = DateTimeOffset.UtcNow,
            Files = [new ScanFileManifest { SourcePath = "source/frame.tif", FileName = "frame.tif", Kind = ScanFileKind.Image, Outcome = ScanFileOutcome.Copied }]
        };
        new OperationStore().WriteManifest(manifest);
        var manifestPath = Path.Combine(manifest.FinalDir, "manifest-finished.json");
        var manifestContents = File.ReadAllText(manifestPath);
        var completed = State(request, ScanOperationStatuses.Completed) with
        {
            Phase = ScanOperationPhases.Complete, FilesCompleted = 1, FileCount = 1,
            Result = new ProcessScanResult(true, 1, manifest, [], [])
        };
        SaveLegacy(completed);
        SaveLegacy(State(Request("waiting"), ScanOperationStatuses.Processing) with { Phase = ScanOperationPhases.Watching });
        SaveLegacy(State(Request("cancelled"), ScanOperationStatuses.Cancelled) with { Phase = ScanOperationPhases.Cancelled });
        var unreadable = Path.Combine(root, "operations", "operation-broken.json");
        File.WriteAllText(unreadable, "{broken");

        var store = new ScanOperationStore(root);
        Assert.True(File.Exists(store.DatabasePath));
        Assert.Equal(1, store.CachedActiveCount);
        Assert.Equal("waiting", Assert.Single(store.ListActive()).IdempotencyKey);
        Assert.Equal(3, store.ListRecent().Count);
        Assert.Equal(1, store.Get("finished").Result!.ImageCount);
        Assert.Equal(ScanOperationStatuses.Cancelled, store.Get("cancelled").Status);
        Assert.Equal(1, store.CachedActiveCount); // Historical reads must not populate the cache.
        Assert.Equal([unreadable], Directory.GetFiles(Path.Combine(root, "operations"), "*.json"));
        Assert.Equal(manifestContents, File.ReadAllText(manifestPath));
        Assert.False(store.Enqueue(request).Created); // Fingerprints remain compatible with the JSON version.

        var restarted = new ScanOperationStore(root);
        Assert.Equal(1, restarted.CachedActiveCount);
        Assert.Equal(3, restarted.ListRecent().Count);
        Assert.Equal(manifestContents, File.ReadAllText(manifestPath));
    }

    [Fact]
    public void RetentionUsesFinishTimePreservesOldActiveJobsAndReclaimsHistory()
    {
        var now = DateTimeOffset.UtcNow;
        SaveLegacy(State(Request("old-active"), ScanOperationStatuses.Processing) with
        {
            CreatedAt = now.AddDays(-400), Phase = ScanOperationPhases.Settling
        });
        var store = new ScanOperationStore(root);
        Finish(store, "old-history", now.AddDays(-31));
        Finish(store, "old-key", now.AddDays(-181), ScanOperationStatuses.Failed);
        Finish(store, "history-boundary", now.AddDays(-30), ScanOperationStatuses.Cancelled);
        Finish(store, "key-boundary", now.AddDays(-180));
        Finish(store, "recently-finished", now.AddDays(-1));

        var cleanup = store.Cleanup(now);
        Assert.Equal(3, cleanup.HistoryDeleted);
        Assert.Equal(1, cleanup.KeysDeleted);
        Assert.Equal(1, store.CachedActiveCount);
        Assert.Equal(ScanOperationPhases.Settling, store.Get("old-active").Phase);
        Assert.Equal(ScanOperationStatuses.Cancelled, store.Get("history-boundary").Status);
        Assert.Equal(ScanOperationStatuses.Completed, store.Get("recently-finished").Status);
        Assert.Throws<ScanOperationHistoryExpiredException>(() => store.Get("old-history"));
        Assert.Throws<ScanOperationHistoryExpiredException>(() => store.Get("key-boundary"));
        Assert.Throws<KeyNotFoundException>(() => store.Get("old-key"));
        Assert.Equal(3, store.ListRecent().Count);
        Assert.Equal(new JobCleanupResult(0, 0), store.Cleanup(now));
    }

    [Theory]
    [InlineData(ScanOperationStatuses.Completed)]
    [InlineData(ScanOperationStatuses.Failed)]
    [InlineData(ScanOperationStatuses.Cancelled)]
    public void ExpiredHistoryStillPreventsDuplicateRequestsAndConflicts(string status)
    {
        var store = new ScanOperationStore(root);
        var request = Request("retained");
        Finish(store, request.IdempotencyKey, DateTimeOffset.UtcNow.AddDays(-31), status);
        store.Cleanup();
        Assert.Equal(0, store.CachedActiveCount);
        Assert.Empty(store.ListRecent());

        var expired = Assert.Throws<ScanOperationHistoryExpiredException>(() => store.Enqueue(request));
        Assert.Equal(status, expired.Status);
        var conflict = store.Enqueue(request with { RollNumber = "2" });
        Assert.True(conflict.Conflict);
        Assert.False(conflict.Created);
        Assert.Empty(store.ListActive());
        var restarted = new ScanOperationStore(root);
        Assert.Throws<ScanOperationHistoryExpiredException>(() => restarted.Get(request.IdempotencyKey));
        Assert.Throws<ScanOperationHistoryExpiredException>(() => restarted.Enqueue(request));
        Assert.Equal(0, restarted.CachedActiveCount);
    }

    [Fact]
    public void KeysExpireAfter180DaysAndAllowNewRequests()
    {
        var store = new ScanOperationStore(root);
        Finish(store, "expired", DateTimeOffset.UtcNow.AddDays(-181));
        store.Cleanup();
        Assert.Throws<KeyNotFoundException>(() => store.Get("expired"));
        Assert.True(store.Enqueue(Request("expired")).Created);
        Assert.Equal(1, store.CachedActiveCount);
    }

    [Fact]
    public void CompletionEvictsCacheAndHistoryReadsStayOnDemand()
    {
        var store = new ScanOperationStore(root);
        store.Enqueue(Request("job"));
        Assert.Equal(1, store.CachedActiveCount);
        store.Update("job", state => state with
        {
            Status = ScanOperationStatuses.Cancelled, Phase = ScanOperationPhases.Cancelled,
            Message = "Cancelled by operator"
        });
        Assert.Equal(0, store.CachedActiveCount);
        Assert.NotNull(store.Get("job").CompletedAt);
        Assert.Single(store.ListRecent());
        Assert.Empty(store.ListActive());
        Assert.Equal(0, new ScanOperationStore(root).CachedActiveCount);
        Assert.False(Directory.Exists(Path.Combine(root, "operations")));
    }

    [Fact]
    public void RecentQueryIncludesAllActiveJobsWithoutLoadingHistoricalPayloads()
    {
        var store = new ScanOperationStore(root);
        for (var i = 0; i < 5; i++) store.Enqueue(Request($"active-{i}"));
        for (var i = 0; i < 8; i++) Finish(store, $"done-{i}", DateTimeOffset.UtcNow);
        // A summary must not parse requests or results, and startup must not load finished payloads.
        Execute(store.DatabasePath, "UPDATE jobs SET request_json='invalid', result_json='invalid' WHERE is_terminal=1");
        var restarted = new ScanOperationStore(root);
        Assert.Equal(5, restarted.CachedActiveCount);
        var recent = restarted.ListRecent(2);
        Assert.Equal(7, recent.Count);
        Assert.All(recent.Take(5), row => Assert.Equal(ScanOperationStatuses.Queued, row.Status));
        Assert.All(recent.Skip(5), row => Assert.Equal(ScanOperationStatuses.Completed, row.Status));
        Assert.Equal(6, restarted.ListRecent(0).Count); // Minimum historical limit is one.
        Assert.Equal(5, restarted.CachedActiveCount);
    }

    [Fact]
    public async Task SeparateStoreInstancesCannotEnqueueTheSameKeyTwice()
    {
        var first = new ScanOperationStore(root);
        var second = new ScanOperationStore(root);
        var results = await Task.WhenAll(Task.Run(() => first.Enqueue(Request("same"))), Task.Run(() => second.Enqueue(Request("same"))));
        Assert.Single(results, result => result.Created);
        Assert.All(results, result => Assert.False(result.Conflict));
        Assert.Single(new ScanOperationStore(root).ListActive());
    }

    [Fact]
    public void RepeatedMigrationDoesNotOverwriteNewerDatabaseState()
    {
        var legacy = State(Request("job"), ScanOperationStatuses.Queued);
        SaveLegacy(legacy);
        var store = new ScanOperationStore(root);
        store.Update("job", state => state with { Status = ScanOperationStatuses.Cancelled, Phase = ScanOperationPhases.Cancelled });
        SaveLegacy(legacy); // Simulate a crash after commit but before deleting the source file.
        var restarted = new ScanOperationStore(root);
        Assert.Equal(ScanOperationStatuses.Cancelled, restarted.Get("job").Status);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "operations")));
        Assert.Empty(restarted.ListActive());
    }

    [Fact]
    public void ConflictingLegacyFileIsPreservedAndMigrationFailsClosed()
    {
        var store = new ScanOperationStore(root);
        store.Enqueue(Request("job"));
        SaveLegacy(State(Request("job") with { RollNumber = "different" }, ScanOperationStatuses.Queued));
        Assert.Throws<InvalidOperationException>(() => new ScanOperationStore(root));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "operations")));
        Assert.Equal("1", store.Get("job").Request.RollNumber);
    }

    [Fact]
    public void NewerSchemaIsNotSilentlyDowngraded()
    {
        var store = new ScanOperationStore(root);
        Execute(store.DatabasePath, "PRAGMA user_version=2");
        Assert.Throws<InvalidOperationException>(() => new ScanOperationStore(root));
    }

    private static ProcessScanRequest Request(string key) => new()
    {
        IdempotencyKey = key, ProfileId = "scanner", OrderNumber = "B123", RollNumber = "1", WaitForReady = true
    };

    private static ScanOperationState State(ProcessScanRequest request, string status) => new()
    {
        OperationId = request.IdempotencyKey, IdempotencyKey = request.IdempotencyKey,
        RequestFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, LegacyJson)))).ToLowerInvariant(),
        Request = request, Status = status,
        CompletedAt = ScanOperationStatuses.IsTerminal(status) ? DateTimeOffset.UtcNow : null
    };

    private static void Finish(ScanOperationStore store, string key, DateTimeOffset completedAt, string status = ScanOperationStatuses.Completed)
    {
        store.Enqueue(Request(key));
        store.Update(key, state => state with { Status = status, Phase = status == ScanOperationStatuses.Completed ? ScanOperationPhases.Complete : status, CompletedAt = completedAt });
    }

    private void SaveLegacy(ScanOperationState operation)
    {
        var directory = Path.Combine(root, "operations");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"operation-{operation.IdempotencyKey}.json"), JsonSerializer.Serialize(operation, LegacyJson));
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
