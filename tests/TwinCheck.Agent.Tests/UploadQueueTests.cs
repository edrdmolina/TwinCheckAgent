using Microsoft.Data.Sqlite;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Tests;

public sealed class UploadQueueTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twincheck-upload-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> queues = [];

    private string WatchA => Path.Combine(root, "sources", "scanner-a");
    private string WatchB => Path.Combine(root, "sources", "scanner-b");
    private string Destination => Path.Combine(root, "destination");
    private string StagingRoot => Path.Combine(root, "staging");
    private string StateDir => Path.Combine(root, "state");

    public UploadQueueTests() => Directory.CreateDirectory(Destination);

    [Fact]
    public void LooseFilesInTheWatchFolderAndProcessedFoldersAreNeverRolls()
    {
        var roll = WriteRoll(WatchA, "roll-a", "frame001.jpg");
        Write(WatchA, "stray.jpg", "loose file in the watch folder");
        Write(Path.Combine(WatchA, "_processed-B999-1"), "frame001.jpg", "waiting for upload");
        var provider = new AgentConfigProvider(Config());

        var candidates = new SourceCandidateService(provider).GetCandidates("scanner-a", null);
        var result = new ScanProcessor(provider, new OperationStore()).Process(Request("op-1", "scanner-a"));

        Assert.Equal(roll, Assert.Single(candidates).Path);
        Assert.Equal(roll, result.Manifest.SourceDir);
        Assert.True(File.Exists(Path.Combine(WatchA, "stray.jpg")));
        Assert.True(Directory.Exists(Path.Combine(WatchA, "_processed-B999-1")));
        Assert.DoesNotContain(result.Manifest.Files, file => file.FileName == "stray.jpg");
    }

    [Fact]
    public void WatchFolderWithOnlyLooseFilesIsRejected()
    {
        Directory.CreateDirectory(WatchA);
        Write(WatchA, "frame001.jpg", "loose file in the watch folder");
        var processor = new ScanProcessor(new AgentConfigProvider(Config()), new OperationStore());

        var exception = Assert.Throws<InvalidOperationException>(() => processor.Process(Request("op-1", "scanner-a")));

        Assert.Contains("no roll folders", exception.Message);
        Assert.True(File.Exists(Path.Combine(WatchA, "frame001.jpg")));
    }

    [Fact]
    public async Task WatcherIgnoresProcessedFoldersWhenCountingRolls()
    {
        Write(Path.Combine(WatchA, "_processed-B999-1"), "frame001.jpg", "waiting for upload");
        var roll = WriteRoll(WatchA, "roll-a", "frame001.jpg");
        using var operations = Operations(out _);

        operations.Enqueue(Request("op-1", "scanner-a") with { WaitForReady = true });
        var completed = await WaitForTerminal(operations, "op-1");

        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(roll, completed.ResolvedSourceDir);
    }

    [Fact]
    public void NotEnoughLocalSpaceFailsBeforeTheRollIsTouched()
    {
        var roll = WriteRoll(WatchA, "roll-a", "frame001.jpg");
        var processor = new ScanProcessor(new AgentConfigProvider(Config()), new OperationStore())
        {
            AvailableSpaceProbe = _ => 0
        };

        var exception = Assert.Throws<IOException>(() => processor.Process(Request("op-1", "scanner-a")));

        Assert.Contains("Not enough local disk space", exception.Message);
        Assert.True(File.Exists(Path.Combine(roll, "frame001.jpg")));
        Assert.Empty(Directory.EnumerateDirectories(WatchA, "_processed-*"));
    }

    [Fact]
    public async Task RollFolderWithAnUnexpectedSubfolderIsKeptWithAWarning()
    {
        var roll = WriteRoll(WatchA, "roll-a", "frame001.jpg");
        Write(Path.Combine(roll, "extras"), "notes.txt", "not uploaded");
        using var operations = Operations(out var queue);

        operations.Enqueue(Request("op-1", "scanner-a"));
        var completed = await WaitForTerminal(operations, "op-1");

        var kept = Path.Combine(WatchA, "_processed-B31009-1");
        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Contains("Kept", completed.Message);
        Assert.True(File.Exists(Path.Combine(kept, "extras", "notes.txt")));
        Assert.Contains("extras", Assert.Single(queue.List()).CleanupWarning);
        Assert.Empty(Directory.EnumerateFileSystemEntries(StagingRoot));
    }

    [Fact]
    public async Task NextRollIsProcessedLocallyWhileTheFirstOneUploads()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg", "frame002.jpg");
        WriteRoll(WatchB, "roll-b", "frame001.jpg");
        using var hold = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        using var operations = Operations(out var queue);
        queue.BeforeFileUpload = (roll, _) =>
        {
            if (roll.IdempotencyKey != "op-a") return;
            holding.Set();
            hold.Wait(TimeSpan.FromSeconds(20));
        };

        operations.Enqueue(Request("op-a", "scanner-a"));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)), "Roll A never started uploading.");
        operations.Enqueue(Request("op-b", "scanner-b", order: "B31010"));
        await WaitUntil(() => operations.Get("op-b").Phase == ScanOperationPhases.Uploading, "Roll B was not processed locally while A uploaded.");

        Assert.Equal(ScanOperationPhases.Uploading, operations.Get("op-a").Phase);
        Assert.Equal(UploadStatuses.Waiting, queue.List().Single(roll => roll.IdempotencyKey == "op-b").Status);
        Assert.False(Directory.Exists(Path.Combine(WatchB, "roll-b")));
        Assert.Equal(0, operations.LocalProcessingCount());
        Assert.Equal(2, queue.PendingCount);

        hold.Set();
        var a = await WaitForTerminal(operations, "op-a");
        var b = await WaitForTerminal(operations, "op-b");
        Assert.Equal(ScanOperationStatuses.Completed, a.Status);
        Assert.Equal(ScanOperationStatuses.Completed, b.Status);
        Assert.True(a.CompletedAt <= b.CompletedAt);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task OnlyOneRollIsProcessedLocallyAtATimeAcrossScanners()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg", "frame002.jpg");
        WriteRoll(WatchB, "roll-b", "frame001.jpg", "frame002.jpg");
        var writer = new ConcurrencyRecordingWriter();
        var provider = new AgentConfigProvider(Config());
        var logger = new LocalAgentLogger(Path.Combine(root, "logs"));
        var store = new ScanOperationStore(StateDir, logger);
        var processor = new ScanProcessor(provider, new OperationStore(), new PassThroughConverter(), writer);
        using var operations = new ScanOperationService(store, processor, new ScanWatchService(provider), logger);
        var metadata = new ScanMetadata { CustomerName = "Jane Smith" };

        operations.Enqueue(Request("op-a", "scanner-a") with { Metadata = metadata });
        operations.Enqueue(Request("op-b", "scanner-b", order: "B31010") with { Metadata = metadata });
        Assert.Equal(ScanOperationStatuses.Completed, (await WaitForTerminal(operations, "op-a")).Status);
        Assert.Equal(ScanOperationStatuses.Completed, (await WaitForTerminal(operations, "op-b")).Status);

        Assert.Equal(4, writer.Calls);
        Assert.Equal(1, writer.MaxConcurrent);
    }

    [Fact]
    public async Task PauseStopsAfterTheCurrentFileAndResumeFinishes()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg", "frame002.jpg", "frame003.jpg");
        using var operations = Operations(out var queue);
        var uploadsStarted = 0;
        queue.BeforeFileUpload = (_, _) =>
        {
            if (Interlocked.Increment(ref uploadsStarted) == 1) queue.Pause();
        };

        operations.Enqueue(Request("op-1", "scanner-a"));
        await WaitUntil(() => queue.List().SingleOrDefault() is { FilesUploaded: 1, Status: UploadStatuses.Waiting }, "The queue did not stop after the first file.");
        await Task.Delay(300);

        var paused = Assert.Single(queue.List());
        Assert.True(paused.Paused);
        Assert.Equal(1, paused.FilesUploaded);
        Assert.Equal(1, uploadsStarted);
        Assert.Equal(ScanOperationPhases.Uploading, operations.Get("op-1").Phase);

        queue.Resume();
        var completed = await WaitForTerminal(operations, "op-1");
        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(3, Directory.EnumerateFiles(FinalDir("B31009"), "*.jpg").Count());
    }

    [Fact]
    public async Task FailedUploadRetriesAndRetryNowSkipsTheWait()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg");
        using var operations = Operations(out var queue, new UploadQueueOptions { RetryBase = TimeSpan.FromHours(1), RetryMax = TimeSpan.FromHours(1) });
        var attempts = 0;
        queue.BeforeFileUpload = (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("NAS refused the write");
        };

        operations.Enqueue(Request("op-1", "scanner-a"));
        await WaitUntil(() => queue.List().SingleOrDefault()?.Status == UploadStatuses.Retrying, "The failed upload was not scheduled for a retry.");

        var retrying = Assert.Single(queue.List());
        Assert.Equal(1, retrying.Attempts);
        Assert.Contains("NAS refused the write", retrying.LastError);
        Assert.True(retrying.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.Contains("retrying", operations.Get("op-1").Message);
        Assert.True(Directory.Exists(Path.Combine(WatchA, "_processed-B31009-1")));

        Assert.True(queue.RetryNow("op-1"));
        var completed = await WaitForTerminal(operations, "op-1");
        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(2, attempts);
        Assert.False(queue.RetryNow("op-1"));
    }

    [Fact]
    public async Task MissingLocalCopyFailsTheRollAndKeepsTheOriginals()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg");
        using var operations = Operations(out var queue);
        queue.BeforeFileUpload = (_, file) => File.Delete(file.StagedPath);

        operations.Enqueue(Request("op-1", "scanner-a"));
        var failed = await WaitForTerminal(operations, "op-1");

        Assert.Equal(ScanOperationStatuses.Failed, failed.Status);
        Assert.Equal("upload-failed", failed.ErrorCode);
        Assert.Equal(UploadStatuses.Failed, Assert.Single(queue.List()).Status);
        Assert.True(File.Exists(Path.Combine(WatchA, "_processed-B31009-1", "frame001.jpg")));
    }

    [Fact]
    public async Task UploadThatStopsMakingProgressIsReportedAsStalled()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg");
        using var hold = new ManualResetEventSlim();
        using var operations = Operations(out var queue, new UploadQueueOptions
        {
            StallAfter = TimeSpan.FromMilliseconds(300),
            StallCheckInterval = TimeSpan.FromMilliseconds(100)
        });
        queue.BeforeFileUpload = (_, _) => hold.Wait(TimeSpan.FromSeconds(20));

        operations.Enqueue(Request("op-1", "scanner-a"));
        await WaitUntil(() => queue.List().SingleOrDefault()?.Stalled == true, "The stalled upload was never reported.");

        Assert.StartsWith("Stalled — NAS not responding", operations.Get("op-1").Message);
        Assert.Equal("frame001.jpg", Assert.Single(queue.List()).CurrentFile);

        hold.Set();
        var completed = await WaitForTerminal(operations, "op-1");
        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.False(Assert.Single(queue.List()).Stalled);
    }

    [Fact]
    public async Task RestartInTheMiddleOfAnUploadResumesIt()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg", "frame002.jpg");
        var store = new ScanOperationStore(StateDir);
        var processor = new ScanProcessor(new AgentConfigProvider(Config()), new OperationStore());
        var roll = IngestAsIfInterrupted(store, processor, "op-1");
        // Frame 1 made it before the agent stopped; frame 2 left a partial file behind.
        var first = processor.UploadFile(roll.Files[0]);
        store.UpdateUploadFile("op-1", first);
        var partial = Path.Combine(FinalDir("B31009"), ".B31009-1-2.jpg.twincheck-partial");
        File.WriteAllText(partial, "interrupted");

        using var operations = Operations(out _);
        var completed = await WaitForTerminal(operations, "op-1");

        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(2, completed.Result!.ImageCount);
        Assert.Equal("frame002.jpg-contents", File.ReadAllText(Path.Combine(FinalDir("B31009"), "B31009-1-2.jpg")));
        Assert.False(File.Exists(partial));
        Assert.False(Directory.Exists(roll.SourceArchiveDir));
        Assert.False(Directory.Exists(roll.StagingDir));
    }

    [Fact]
    public async Task RestartAfterTheManifestWasWrittenFinishesCleanup()
    {
        WriteRoll(WatchA, "roll-a", "frame001.jpg");
        var store = new ScanOperationStore(StateDir);
        var processor = new ScanProcessor(new AgentConfigProvider(Config()), new OperationStore());
        var roll = IngestAsIfInterrupted(store, processor, "op-1");
        var uploaded = processor.UploadFile(roll.Files[0]);
        store.UpdateUploadFile("op-1", uploaded);
        Assert.Throws<IOException>(() => processor.FinishUpload(roll with { Files = [uploaded] }, written =>
        {
            store.UpdateUploadRoll(written);
            throw new IOException("Agent stopped right after writing the manifest.");
        }));
        Assert.True(Directory.Exists(roll.SourceArchiveDir));

        using var operations = Operations(out var queue);
        var completed = await WaitForTerminal(operations, "op-1");

        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.False(Directory.Exists(roll.SourceArchiveDir));
        Assert.Equal(UploadStatuses.Done, Assert.Single(queue.List()).Status);
    }

    [Fact]
    public void HandoffInterruptedBeforeTheRenameIsForgottenSoTheRollIsProcessedAgain()
    {
        var rollFolder = WriteRoll(WatchA, "roll-a", "frame001.jpg");
        var store = new ScanOperationStore(StateDir);
        var processor = new ScanProcessor(new AgentConfigProvider(Config()), new OperationStore());
        var logger = new LocalAgentLogger(Path.Combine(root, "logs"));
        Assert.Throws<IOException>(() => processor.Ingest(Request("op-1", "scanner-a"), beforeHandoff: roll =>
        {
            store.SaveUpload(roll);
            throw new IOException("Agent stopped before the rename.");
        }));
        Assert.Equal(UploadStatuses.Handoff, store.GetUpload("op-1")?.Status);

        var queue = new UploadQueueService(store, processor, new ScanWatchService(new AgentConfigProvider(Config())), logger);
        queues.Add(queue);

        Assert.Null(store.GetUpload("op-1"));
        Assert.True(File.Exists(Path.Combine(rollFolder, "frame001.jpg")));
    }

    [Fact]
    public void CleanupDeletesOnlyFoldersWhoseFilesAreVerified()
    {
        var config = Config();
        var operationStore = new OperationStore();
        var finalDir = Path.Combine(Destination, "old-week", "B1", "B1-1");
        Write(finalDir, "B1-1-1.tif", "delivered");
        var verifiedArchive = Path.Combine(Destination, "_processed", "roll-1-old-op");
        Write(verifiedArchive, "scan001.bmp", "original");
        operationStore.WriteManifest(Manifest("old-op", finalDir, verifiedArchive, Path.Combine(finalDir, "B1-1-1.tif"), "delivered".Length));
        var orphanArchive = Path.Combine(Destination, "_processed", "roll-2-unknown");
        Write(orphanArchive, "scan001.bmp", "original");
        var mismatchFinal = Path.Combine(Destination, "old-week", "B2", "B2-1");
        Write(mismatchFinal, "B2-1-1.tif", "truncated");
        var mismatchArchive = Path.Combine(Destination, "_processed", "roll-3-bad");
        Write(mismatchArchive, "scan001.bmp", "original");
        operationStore.WriteManifest(Manifest("bad-op", mismatchFinal, mismatchArchive, Path.Combine(mismatchFinal, "B2-1-1.tif"), 999));
        var cleanup = new CleanupService(new AgentConfigProvider(config), new ScanOperationStore(StateDir), new DestinationWriteLock(), new LocalAgentLogger(Path.Combine(root, "logs")));

        var entries = cleanup.List();

        Assert.Equal(3, entries.Count);
        Assert.True(entries.Single(entry => entry.Path == verifiedArchive).Verified);
        Assert.False(entries.Single(entry => entry.Path == orphanArchive).Verified);
        Assert.Contains("wrong size", entries.Single(entry => entry.Path == mismatchArchive).Reason);
        Assert.All(entries, entry => Assert.Equal(CleanupKinds.LegacyArchive, entry.Kind));

        var result = cleanup.Delete([verifiedArchive, orphanArchive, mismatchArchive, Path.Combine(root, "elsewhere")]);

        Assert.Equal([verifiedArchive], result.Deleted);
        Assert.Equal(3, result.Skipped.Count);
        Assert.False(Directory.Exists(verifiedArchive));
        Assert.True(Directory.Exists(orphanArchive));
        Assert.True(Directory.Exists(mismatchArchive));
    }

    [Fact]
    public async Task CleanupListsKeptRollFoldersButNotOnesStillUploading()
    {
        var kept = WriteRoll(WatchA, "roll-a", "frame001.jpg");
        Write(Path.Combine(kept, "extras"), "notes.txt", "not uploaded");
        WriteRoll(WatchB, "roll-b", "frame001.jpg");
        using var hold = new ManualResetEventSlim();
        using var operations = Operations(out var queue);
        queue.BeforeFileUpload = (roll, _) =>
        {
            if (roll.IdempotencyKey == "op-b") hold.Wait(TimeSpan.FromSeconds(20));
        };
        operations.Enqueue(Request("op-a", "scanner-a"));
        await WaitForTerminal(operations, "op-a");
        operations.Enqueue(Request("op-b", "scanner-b", order: "B31010"));
        await WaitUntil(() => queue.List().Any(roll => roll.IdempotencyKey == "op-b" && roll.Status == UploadStatuses.Uploading), "Roll B never started uploading.");
        var cleanup = new CleanupService(new AgentConfigProvider(Config()), new ScanOperationStore(StateDir), new DestinationWriteLock(), new LocalAgentLogger(Path.Combine(root, "logs")));

        var entry = Assert.Single(cleanup.List());
        hold.Set();

        Assert.Equal(Path.Combine(WatchA, "_processed-B31009-1"), entry.Path);
        Assert.Equal(CleanupKinds.KeptProcessed, entry.Kind);
        Assert.True(entry.Verified);
        await WaitForTerminal(operations, "op-b");
    }

    [Fact]
    public void SchemaVersionOneDatabaseIsUpgradedKeepingJobs()
    {
        var store = new ScanOperationStore(StateDir);
        store.Enqueue(Request("op-old", "scanner-a"));
        Execute(store.DatabasePath, "DROP TABLE upload_files; DROP TABLE upload_rolls; DROP TABLE agent_settings; PRAGMA user_version=1;");

        var upgraded = new ScanOperationStore(StateDir);
        upgraded.UploadsPaused = true;

        Assert.Equal(ScanOperationStatuses.Queued, upgraded.Get("op-old").Status);
        Assert.Empty(upgraded.ListUploads());
        Assert.True(new ScanOperationStore(StateDir).UploadsPaused);
    }

    private UploadRoll IngestAsIfInterrupted(ScanOperationStore store, ScanProcessor processor, string key)
    {
        var request = Request(key, "scanner-a");
        store.Enqueue(request);
        var ingested = processor.Ingest(request, beforeHandoff: store.SaveUpload);
        var roll = ingested.Upload!;
        store.UpdateUploadRoll(roll);
        store.Update(key, job => job with
        {
            Status = ScanOperationStatuses.Processing,
            Phase = ScanOperationPhases.Uploading,
            ResolvedSourceDir = roll.SourceDir
        });
        return roll;
    }

    private ScanOperationService Operations(out UploadQueueService queue, UploadQueueOptions? options = null)
    {
        var provider = new AgentConfigProvider(Config());
        var logger = new LocalAgentLogger(Path.Combine(root, "logs"));
        var store = new ScanOperationStore(StateDir, logger);
        var processor = new ScanProcessor(provider, new OperationStore());
        var watch = new ScanWatchService(provider);
        queue = new UploadQueueService(store, processor, watch, logger, options);
        queues.Add(queue);
        return new ScanOperationService(store, processor, watch, logger, queue);
    }

    private AgentConfig Config() => new()
    {
        ApiKey = "test-key",
        AllowedSourceRoots = [Path.Combine(root, "sources")],
        AllowedDestinationRoots = [Destination],
        StagingDir = StagingRoot,
        Profiles =
        [
            Profile("scanner-a", WatchA),
            Profile("scanner-b", WatchB)
        ]
    };

    private ScannerProfile Profile(string id, string watch) => new()
    {
        Id = id,
        Name = id,
        SourceDir = watch,
        DestinationDir = Destination,
        ScannerMode = ScannerModes.FrontierPollingWatch,
        SettleStableSeconds = 0,
        SettleTimeoutSeconds = 10,
        WatchTimeoutSeconds = 10,
        SettlePollSeconds = 1
    };

    private static ProcessScanRequest Request(string key, string profile, string order = "B31009") => new()
    {
        IdempotencyKey = key,
        ProfileId = profile,
        OrderNumber = order,
        RollNumber = "1"
    };

    private string FinalDir(string order) =>
        Path.Combine(Destination, ScannerFileSystem.GetWeekFolder(), order, $"{order}-1");

    private static OperationManifest Manifest(string key, string finalDir, string archive, string delivered, long size) => new()
    {
        IdempotencyKey = key,
        ProfileId = "scanner-a",
        OrderNumber = "B1",
        RollNumber = "1",
        SourceDir = "/old/source",
        DestinationDir = "/old/destination",
        FinalDir = finalDir,
        SourceArchiveDir = archive,
        Ok = true,
        CompletedAt = DateTimeOffset.UtcNow.AddDays(-40),
        Files =
        [
            new ScanFileManifest
            {
                SourcePath = "/old/source/scan001.bmp", DestinationPath = delivered, FileName = "scan001.bmp",
                Size = size, Kind = ScanFileKind.Image, Outcome = ScanFileOutcome.Copied
            }
        ]
    };

    private string WriteRoll(string watch, string name, params string[] files)
    {
        var folder = Path.Combine(watch, name);
        foreach (var file in files) Write(folder, file, $"{file}-contents");
        return folder;
    }

    private static void Write(string directory, string name, string contents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), contents);
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task WaitUntil(Func<bool> condition, string failure, int timeoutSeconds = 15)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, failure);
            await Task.Delay(50);
        }
    }

    private static async Task<ScanOperationState> WaitForTerminal(ScanOperationService service, string key, int timeoutSeconds = 15)
    {
        await WaitUntil(() => ScanOperationStatuses.IsTerminal(service.Get(key).Status), $"Operation {key} never finished.", timeoutSeconds);
        return service.Get(key);
    }

    public void Dispose()
    {
        foreach (var queue in queues) queue.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class ConcurrencyRecordingWriter : IScanMetadataWriter
    {
        private int active;
        public int Calls;
        public int MaxConcurrent;
        public bool IsAvailable => true;

        public void Write(string stagedPath, PhotoMetadata metadata)
        {
            var now = Interlocked.Increment(ref active);
            Interlocked.Increment(ref Calls);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref active);
        }
    }

    private sealed class PassThroughConverter : IScanImageConverter
    {
        public void ConvertBmpToTiff(string sourcePath, string destinationPath) => File.Copy(sourcePath, destinationPath);
    }
}
