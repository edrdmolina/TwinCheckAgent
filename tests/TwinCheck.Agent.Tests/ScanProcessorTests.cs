using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Tests;

public sealed class ScanProcessorTests
{
    [Fact]
    public void DryRunPlansMappingsWithoutMovingSource()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.jpg", "image-one");
        workspace.WriteFile(sourceDir, "notes.txt", "operator note");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var result = processor.Process(workspace.CreateRequest(dryRun: true));

        Assert.True(result.Ok);
        Assert.Equal(1, result.ImageCount);
        Assert.Equal(workspace.FinalDir(destinationRoot), result.Manifest.FinalDir);
        Assert.Contains(result.Manifest.Files, file => file.DestinationPath == Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-1.jpg"));
        Assert.Contains(result.Reviewed, file => file.FileName == "notes.txt");
        Assert.True(Directory.Exists(sourceDir));
        Assert.False(Directory.Exists(workspace.FinalDir(destinationRoot)));
    }

    [Fact]
    public void ProcessCopiesVerifiesReviewsAndArchivesSource()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.jpg", "image-one");
        workspace.WriteFile(sourceDir, "metadata.json", "{}");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var result = processor.Process(workspace.CreateRequest());

        var finalImage = Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-1.jpg");
        var reviewFile = Path.Combine(destinationRoot, "_review", "op-1", "metadata.json");
        var processedRoot = Path.Combine(destinationRoot, "_processed");

        Assert.True(result.Ok);
        Assert.True(File.Exists(finalImage));
        Assert.Equal("image-one", File.ReadAllText(finalImage));
        Assert.True(File.Exists(reviewFile));
        Assert.False(Directory.Exists(sourceDir));
        Assert.Contains(Directory.EnumerateDirectories(processedRoot), path => path.Contains("roll-a-op-1"));
        Assert.True(File.Exists(Path.Combine(workspace.FinalDir(destinationRoot), "manifest-op-1.json")));
    }

    [Fact]
    public void DuplicateNameWithDifferentBytesWritesVersionedFile()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        var finalDir = workspace.FinalDir(destinationRoot);
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "B31009-1-1.jpg"), "existing-different-image");
        workspace.WriteFile(sourceDir, "scan001.jpg", "new-image");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var result = processor.Process(workspace.CreateRequest());

        Assert.Single(result.Conflicts);
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-1.jpg")));
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-1-v2.jpg")));
        Assert.Equal("new-image", File.ReadAllText(Path.Combine(finalDir, "B31009-1-1-v2.jpg")));
    }

    [Fact]
    public void DuplicateNameWithIdenticalBytesIsIdempotentNoOp()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        var finalDir = workspace.FinalDir(destinationRoot);
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "B31009-1-1.jpg"), "same-image");
        workspace.WriteFile(sourceDir, "scan001.jpg", "same-image");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var result = processor.Process(workspace.CreateRequest());

        Assert.Empty(result.Conflicts);
        Assert.Contains(result.Manifest.Files, file => file.Outcome == ScanFileOutcome.AlreadyDone);
        Assert.False(File.Exists(Path.Combine(finalDir, "B31009-1-1-v2.jpg")));
    }

    [Fact]
    public void RetryWithSameIdempotencyKeyReturnsStoredManifest()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.jpg", "image-one");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var first = processor.Process(workspace.CreateRequest());
        var second = processor.Process(workspace.CreateRequest(sourceOverride: Path.Combine(destinationRoot, "_processed", "roll-a-op-1")));

        Assert.Equal(first.Manifest.IdempotencyKey, second.Manifest.IdempotencyKey);
        Assert.Equal(first.Manifest.CompletedAt, second.Manifest.CompletedAt);
    }

    [Fact]
    public void PendingManifestRecoversWhenSourceWasAlreadyArchived()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.bmp", "large-image-placeholder");
        var operationStore = new OperationStore();
        var processor = new ScanProcessor(workspace.CreateConfig(sourceDir, destinationRoot), operationStore);

        var first = processor.Process(workspace.CreateRequest());
        var manifestPath = Path.Combine(first.Manifest.FinalDir, "manifest-op-1.json");
        operationStore.WriteManifestAt(manifestPath, first.Manifest with { CompletedAt = null });

        var recovered = processor.Process(workspace.CreateRequest());

        Assert.True(recovered.Ok);
        Assert.NotNull(recovered.Manifest.CompletedAt);
        Assert.False(Directory.Exists(sourceDir));
        Assert.True(Directory.Exists(recovered.Manifest.SourceArchiveDir));
    }

    [Fact]
    public void CrossFilesystemArchiveFallbackCopiesVerifiesAndDeletesSource()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var archiveDir = Path.Combine(workspace.CreateDestination(), "_processed", "roll-a-op-1");
        workspace.WriteFile(sourceDir, "scan001.bmp", "large-image-placeholder");
        workspace.WriteFile(Path.Combine(sourceDir, "metadata"), "notes.txt", "operator-note");

        ScanProcessor.ArchiveSourceFolder(sourceDir, archiveDir, forceCopy: true);

        Assert.False(Directory.Exists(sourceDir));
        Assert.Equal("large-image-placeholder", File.ReadAllText(Path.Combine(archiveDir, "scan001.bmp")));
        Assert.Equal("operator-note", File.ReadAllText(Path.Combine(archiveDir, "metadata", "notes.txt")));
        Assert.False(Directory.Exists($"{archiveDir}.partial"));
    }

    [Fact]
    public void OperationStoreRejectsConflictingIdempotencyPayload()
    {
        using var workspace = new TempWorkspace();
        var store = new ScanOperationStore(Path.Combine(workspace.Root, "state"));
        var first = store.Enqueue(workspace.CreateRequest());
        var duplicate = store.Enqueue(workspace.CreateRequest());
        var conflict = store.Enqueue(workspace.CreateRequest() with { RollNumber = "2" });

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.False(duplicate.Conflict);
        Assert.True(conflict.Conflict);
    }

    [Fact]
    public async Task ConcurrentEnqueueCreatesOneOperationForIdempotencyKey()
    {
        using var workspace = new TempWorkspace();
        var store = new ScanOperationStore(Path.Combine(workspace.Root, "state"));
        var request = workspace.CreateRequest();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => store.Enqueue(request))));

        Assert.Single(results, result => result.Created);
        Assert.All(results, result => Assert.False(result.Conflict));
        Assert.Single(store.List());
    }

    [Fact]
    public async Task AsyncOperationCompletesAndPersistsStatus()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.bmp", "large-image-placeholder");
        var configProvider = new AgentConfigProvider(workspace.CreateConfig(sourceDir, destinationRoot));
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        var stateDir = Path.Combine(workspace.Root, "state");
        var stateStore = new ScanOperationStore(stateDir, logger);
        var processor = new ScanProcessor(configProvider, new OperationStore());
        var watchService = new ScanWatchService(configProvider);
        using var operations = new ScanOperationService(stateStore, processor, watchService, logger);

        var queued = operations.Enqueue(workspace.CreateRequest());
        Assert.True(queued.Created);

        ScanOperationState state;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(100);
            state = operations.Get("op-1");
        } while (!ScanOperationStatuses.IsTerminal(state.Status) && DateTimeOffset.UtcNow < deadline);

        Assert.Equal(ScanOperationStatuses.Completed, state.Status);
        Assert.NotNull(state.Result);
        var reloaded = new ScanOperationStore(stateDir).Get("op-1");
        Assert.Equal(ScanOperationStatuses.Completed, reloaded.Status);
    }

    [Fact]
    public async Task AsyncOperationRequeuesPersistedInProgressStateAfterRestart()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.bmp", "large-image-placeholder");
        var configProvider = new AgentConfigProvider(workspace.CreateConfig(sourceDir, destinationRoot));
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        var stateDir = Path.Combine(workspace.Root, "state");
        var firstStore = new ScanOperationStore(stateDir, logger);
        firstStore.Enqueue(workspace.CreateRequest());
        firstStore.Update("op-1", operation => operation with
        {
            Status = ScanOperationStatuses.Processing,
            Phase = ScanOperationPhases.Copying,
            StartedAt = DateTimeOffset.UtcNow
        });

        using var recoveredService = new ScanOperationService(
            new ScanOperationStore(stateDir, logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger);

        ScanOperationState state;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(100);
            state = recoveredService.Get("op-1");
        } while (!ScanOperationStatuses.IsTerminal(state.Status) && DateTimeOffset.UtcNow < deadline);

        Assert.Equal(ScanOperationStatuses.Completed, state.Status);
        Assert.NotNull(state.Result);
    }

    [Fact]
    public void RejectsSourceOutsideAllowedRoots()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        var outsideSource = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outsideSource);

        var config = workspace.CreateConfig(sourceDir, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(outsideSource, destinationRoot)
            ]
        };
        var processor = new ScanProcessor(config, new OperationStore());

        var exception = Assert.Throws<InvalidOperationException>(() => processor.Process(workspace.CreateRequest()));
        Assert.Contains("outside the configured allowed roots", exception.Message);
    }

    [Fact]
    public void SourceRootWithSingleRollSubfolderProcessesAndArchivesOnlyRollFolder()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Target");
        var rollFolder = Path.Combine(sourceRoot, "B31485-8");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");
        workspace.WriteFile(rollFolder, "frame002.tif", "image-two");

        var processor = workspace.CreateProcessor(sourceRoot, destinationRoot);
        var result = processor.Process(workspace.CreateRequest());

        Assert.True(result.Ok);
        Assert.Equal(2, result.ImageCount);
        Assert.True(Directory.Exists(sourceRoot));
        Assert.False(Directory.Exists(rollFolder));
        Assert.True(File.Exists(Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-1.tif")));
        Assert.True(File.Exists(Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-2.tif")));
        Assert.Contains(
            Directory.EnumerateDirectories(Path.Combine(destinationRoot, "_processed")),
            path => path.Contains("B31485-8-op-1"));
        Assert.Equal(rollFolder, result.Manifest.SourceDir);
    }

    [Fact]
    public void SourceRootWithMultipleRollSubfoldersRefusesToGuess()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Target");
        var firstRollFolder = Path.Combine(sourceRoot, "B31485-8");
        var secondRollFolder = Path.Combine(sourceRoot, "B31485-9");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(firstRollFolder, "frame001.tif", "image-one");
        workspace.WriteFile(secondRollFolder, "frame001.tif", "image-two");

        var processor = workspace.CreateProcessor(sourceRoot, destinationRoot);
        var exception = Assert.Throws<MultipleSourceCandidatesException>(() => processor.Process(workspace.CreateRequest()));

        Assert.Contains("multiple candidate roll folders", exception.Message);
        Assert.True(Directory.Exists(sourceRoot));
        Assert.True(Directory.Exists(firstRollFolder));
        Assert.True(Directory.Exists(secondRollFolder));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "_processed")));
    }

    [Fact]
    public void CandidateServiceListsRollSubfoldersWithImageCounts()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Target");
        var firstRollFolder = Path.Combine(sourceRoot, "B31485-8");
        var secondRollFolder = Path.Combine(sourceRoot, "B31485-9");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(firstRollFolder, "frame001.tif", "image-one");
        workspace.WriteFile(firstRollFolder, "frame002.tif", "image-two");
        workspace.WriteFile(secondRollFolder, "frame001.tif", "image-three");
        workspace.WriteFile(secondRollFolder, "notes.txt", "not counted");

        var service = new SourceCandidateService(new AgentConfigProvider(workspace.CreateConfig(sourceRoot, destinationRoot)));
        var candidates = service.GetCandidates("dev-profile", null);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate =>
            candidate.Name == "B31485-8"
            && candidate.Path == firstRollFolder
            && candidate.ImageCount == 2
            && !candidate.IsConfiguredRoot
            && candidate.ScannerMode == ScannerModes.FrontierPollingWatch);
        Assert.Contains(candidates, candidate =>
            candidate.Name == "B31485-9"
            && candidate.Path == secondRollFolder
            && candidate.ImageCount == 1
            && !candidate.IsConfiguredRoot);
    }

    [Fact]
    public void ProcessIgnoresExportSentinelFile()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.jpg", "image-one");
        workspace.WriteFile(sourceDir, FileSystemSafety.ExportSentinelFileName, "{}");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var result = processor.Process(workspace.CreateRequest());

        Assert.True(result.Ok);
        Assert.Equal(1, result.ImageCount);
        Assert.Empty(result.Reviewed);
        Assert.DoesNotContain(result.Manifest.Files, file => file.FileName == FileSystemSafety.ExportSentinelFileName);
    }

    [Fact]
    public void ScannerModeAliasesNormalizeToNewValues()
    {
        Assert.Equal(ScannerModes.FrontierPollingWatch, ScannerModes.Normalize(ScannerModes.FrontierFolder));
        Assert.Equal(ScannerModes.NoritsuWatch, ScannerModes.Normalize(ScannerModes.NoritsuDailyWatch));
        Assert.Equal(ScannerModes.FrontierSentinelWatch, ScannerModes.Normalize(ScannerModes.FrontierSentinelWatch));
    }

    [Fact]
    public void LocalAgentLoggerWritesAndReadsRecentLines()
    {
        using var workspace = new TempWorkspace();
        var logDir = Path.Combine(workspace.Root, "logs");
        var logger = new LocalAgentLogger(logDir);

        logger.Info("first");
        logger.Warning("second");
        logger.Error("third");

        var recent = logger.ReadRecentLines(2);

        Assert.Equal(2, recent.Count);
        Assert.Contains("second", recent[0]);
        Assert.Contains("third", recent[1]);
        Assert.True(Directory.Exists(logDir));
    }

    [Fact]
    public void LocalAgentConfigPathCanBeOverriddenByEnvironment()
    {
        var original = Environment.GetEnvironmentVariable(LocalAgentConfigStore.ConfigPathEnvironmentVariable);
        using var workspace = new TempWorkspace();
        var configPath = Path.Combine(workspace.Root, "shared-config", "agent-config.json");

        try
        {
            Environment.SetEnvironmentVariable(LocalAgentConfigStore.ConfigPathEnvironmentVariable, configPath);

            Assert.Equal(Path.GetFullPath(configPath), LocalAgentConfigStore.ConfigPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAgentConfigStore.ConfigPathEnvironmentVariable, original);
        }
    }

    [Fact]
    public void LocalAgentLogDirectoryCanBeOverriddenByEnvironment()
    {
        var original = Environment.GetEnvironmentVariable(LocalAgentLogger.LogDirectoryEnvironmentVariable);
        using var workspace = new TempWorkspace();
        var logDir = Path.Combine(workspace.Root, "shared-logs");

        try
        {
            Environment.SetEnvironmentVariable(LocalAgentLogger.LogDirectoryEnvironmentVariable, logDir);

            Assert.Equal(Path.GetFullPath(logDir), LocalAgentLogger.DefaultLogDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAgentLogger.LogDirectoryEnvironmentVariable, original);
        }
    }

    [Fact]
    public void DiagnosticsServiceReportsProfileReadiness()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        var config = workspace.CreateConfig(sourceDir, destinationRoot);
        var service = new DiagnosticsService(new AgentConfigProvider(config), new OperationStore());

        var diagnostics = service.GetDiagnostics("https://localhost:3625");

        Assert.Equal("https://localhost:3625", diagnostics.AgentUrl);
        Assert.Single(diagnostics.Profiles);
        Assert.Equal("Ready", diagnostics.Profiles[0].Readiness);
        Assert.Equal(ScannerModes.FrontierPollingWatch, diagnostics.Profiles[0].ScannerMode);
    }

    [Fact]
    public void RescanWritesSiblingRescanFolder()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "scan001.jpg", "image-one");

        var processor = workspace.CreateProcessor(sourceDir, destinationRoot);
        var result = processor.Process(workspace.CreateRequest(scanKind: ScanKinds.Rescan, rescanNumber: 2));

        Assert.Equal(Path.Combine(destinationRoot, ScannerFileSystem.GetWeekFolder(), "B31009", "B31009-1-rescan-2"), result.Manifest.FinalDir);
        Assert.True(File.Exists(Path.Combine(result.Manifest.FinalDir, "B31009-1-1.jpg")));
    }

    [Fact]
    public async Task NoritsuWatchDetectsNewDailyChildFolder()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Noritsu");
        var destinationRoot = workspace.CreateDestination();
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.NoritsuWatch,
                    SettleStableSeconds = 0,
                    SettleTimeoutSeconds = 5,
                    SettlePollSeconds = 1
                }
            ]
        };
        var service = new ScanWatchService(new AgentConfigProvider(config));
        var watch = service.Start(new StartScanWatchRequest
        {
            ProfileId = "dev-profile",
            OrderNumber = "B31009",
            RollNumber = "1"
        });

        var rollFolder = Path.Combine(watch.WatchDir, "roll-from-scanner");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");

        ScanWatchState state;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(250);
            state = service.Get(watch.WatchId);
        } while (state.Status is not "ready" && DateTimeOffset.UtcNow < deadline);

        Assert.Equal("ready", state.Status);
        Assert.NotNull(state.Candidate);
        Assert.Equal(1, state.Candidate.ImageCount);
        Assert.Equal(rollFolder, state.Candidate.Path);
        Assert.Equal(ScannerModes.NoritsuWatch, state.Candidate.ScannerMode);
    }

    [Fact]
    public async Task FrontierPollingWatchDetectsNewChildFolder()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierPollingWatch,
                    SettleStableSeconds = 0,
                    SettleTimeoutSeconds = 5,
                    SettlePollSeconds = 1
                }
            ]
        };
        var service = new ScanWatchService(new AgentConfigProvider(config));
        var watch = service.Start(new StartScanWatchRequest
        {
            ProfileId = "dev-profile",
            OrderNumber = "B31009",
            RollNumber = "1"
        });

        var rollFolder = Path.Combine(sourceRoot, "B31485-8");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");

        var state = await WaitForReady(service, watch.WatchId);

        Assert.Equal("ready", state.Status);
        Assert.NotNull(state.Candidate);
        Assert.Equal(1, state.Candidate.ImageCount);
        Assert.Equal(rollFolder, state.Candidate.Path);
        Assert.Equal(ScannerModes.FrontierPollingWatch, state.Candidate.ScannerMode);
    }

    [Fact]
    public async Task FrontierSentinelWatchWaitsForExportDone()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch,
                    SettleStableSeconds = 0,
                    SettleTimeoutSeconds = 5,
                    SettlePollSeconds = 1
                }
            ]
        };
        var service = new ScanWatchService(new AgentConfigProvider(config));
        var watch = service.Start(new StartScanWatchRequest
        {
            ProfileId = "dev-profile",
            OrderNumber = "B31009",
            RollNumber = "1"
        });

        var rollFolder = Path.Combine(sourceRoot, "B31485-8");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");
        await Task.Delay(500);
        Assert.NotEqual("ready", service.Get(watch.WatchId).Status);

        workspace.WriteFile(rollFolder, FileSystemSafety.ExportSentinelFileName, "{}");

        var state = await WaitForReady(service, watch.WatchId);

        Assert.Equal("ready", state.Status);
        Assert.NotNull(state.Candidate);
        Assert.Equal(1, state.Candidate.ImageCount);
        Assert.Equal(rollFolder, state.Candidate.Path);
        Assert.Equal(ScannerModes.FrontierSentinelWatch, state.Candidate.ScannerMode);
    }

    [Fact]
    public void DirectSentinelProcessingRejectsMissingExportDone()
    {
        using var workspace = new TempWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteFile(sourceDir, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceDir, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceDir, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch
                }
            ]
        };
        var processor = new ScanProcessor(config, new OperationStore());

        var exception = Assert.Throws<ScanReadinessException>(() => processor.Process(workspace.CreateRequest()));

        Assert.Equal("source-not-ready", exception.Code);
        Assert.True(Directory.Exists(sourceDir));
        Assert.False(Directory.Exists(workspace.FinalDir(destinationRoot)));
    }

    [Fact]
    public async Task DurableSentinelOperationWaitsForNewestExistingExportAndProcessesEveryImage()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var olderReady = Path.Combine(sourceRoot, "100-old-Ready");
        workspace.WriteFile(olderReady, "old.tif", "old-image");
        workspace.WriteFile(olderReady, FileSystemSafety.ExportSentinelFileName, "{}");
        File.SetLastWriteTimeUtc(olderReady, DateTime.UtcNow.AddMinutes(-10));

        var waitFolder = Path.Combine(sourceRoot, "200-WAIT");
        workspace.WriteFile(waitFolder, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch,
                    SettleStableSeconds = 0,
                    SettleTimeoutSeconds = 5,
                    WatchTimeoutSeconds = 10,
                    SettlePollSeconds = 1
                }
            ]
        };
        var configProvider = new AgentConfigProvider(config);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        using var operations = new ScanOperationService(
            new ScanOperationStore(Path.Combine(workspace.Root, "state"), logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger);

        operations.Enqueue(workspace.CreateRequest() with { WaitForReady = true });
        await Task.Delay(500);
        Assert.False(ScanOperationStatuses.IsTerminal(operations.Get("op-1").Status));
        workspace.WriteFile(waitFolder, "frame002.tif", "image-two");
        var readyFolder = Path.Combine(sourceRoot, "200-Ready");
        Directory.Move(waitFolder, readyFolder);
        workspace.WriteFile(readyFolder, FileSystemSafety.ExportSentinelFileName, "{}");

        var completed = await WaitForTerminal(operations, "op-1");

        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(2, completed.Result?.ImageCount);
        Assert.Equal(readyFolder, completed.ResolvedSourceDir);
        Assert.True(Directory.Exists(olderReady));
    }

    [Fact]
    public async Task DurableSentinelWatchTimeoutFailsWithoutMovingSource()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var waitFolder = Path.Combine(sourceRoot, "roll-WAIT");
        workspace.WriteFile(waitFolder, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch,
                    SettleStableSeconds = 0,
                    SettleTimeoutSeconds = 2,
                    WatchTimeoutSeconds = 1,
                    SettlePollSeconds = 1
                }
            ]
        };
        var configProvider = new AgentConfigProvider(config);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        using var operations = new ScanOperationService(
            new ScanOperationStore(Path.Combine(workspace.Root, "state"), logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger);

        operations.Enqueue(workspace.CreateRequest() with { WaitForReady = true });
        var failed = await WaitForTerminal(operations, "op-1", timeoutSeconds: 5);

        Assert.Equal(ScanOperationStatuses.Failed, failed.Status);
        Assert.Equal("watch-timeout", failed.ErrorCode);
        Assert.True(Directory.Exists(waitFolder));
        Assert.False(Directory.Exists(workspace.FinalDir(destinationRoot)));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "_processed")));
    }

    [Fact]
    public async Task CancellingWaitingSentinelOperationKeepsSourceAndDoesNotResumeAfterRestart()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var rollFolder = Path.Combine(sourceRoot, "roll-WAIT");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch,
                    WatchTimeoutSeconds = 10
                }
            ]
        };
        var configProvider = new AgentConfigProvider(config);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        var stateDir = Path.Combine(workspace.Root, "state");
        using (var operations = new ScanOperationService(
            new ScanOperationStore(stateDir, logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger))
        {
            operations.Enqueue(workspace.CreateRequest() with { WaitForReady = true });
            Assert.Single(operations.ListActive());
            var cancelled = operations.Cancel("op-1");
            Assert.Equal(ScanOperationStatuses.Cancelled, cancelled?.Status);
            Assert.Empty(operations.ListActive());
            Assert.Equal(ScanOperationStatuses.Cancelled, Assert.Single(operations.ListRecent()).Status);
        }

        workspace.WriteFile(rollFolder, FileSystemSafety.ExportSentinelFileName, "{}");
        using var recovered = new ScanOperationService(
            new ScanOperationStore(stateDir, logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger);
        await Task.Delay(1200);

        Assert.Equal(ScanOperationStatuses.Cancelled, recovered.Get("op-1").Status);
        Assert.True(Directory.Exists(rollFolder));
        Assert.False(Directory.Exists(workspace.FinalDir(destinationRoot)));
    }

    [Fact]
    public async Task CancellingQueuedOperationDoesNotConsumeNextSentinelFolder()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var rollFolder = Path.Combine(sourceRoot, "roll-WAIT");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch,
                    SettleStableSeconds = 0,
                    WatchTimeoutSeconds = 10
                }
            ]
        };
        var provider = new AgentConfigProvider(config);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        using var operations = new ScanOperationService(
            new ScanOperationStore(Path.Combine(workspace.Root, "state"), logger),
            new ScanProcessor(provider, new OperationStore()),
            new ScanWatchService(provider),
            logger);

        operations.Enqueue(workspace.CreateRequest() with { WaitForReady = true });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (operations.Get("op-1").Phase != ScanOperationPhases.Watching && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal(ScanOperationPhases.Watching, operations.Get("op-1").Phase);

        operations.Enqueue(workspace.CreateRequest() with
        {
            IdempotencyKey = "op-2",
            OrderNumber = "B31010",
            WaitForReady = true
        });
        Assert.Equal(ScanOperationStatuses.Cancelled, operations.Cancel("op-2")?.Status);
        workspace.WriteFile(rollFolder, FileSystemSafety.ExportSentinelFileName, "{}");

        var completed = await WaitForTerminal(operations, "op-1");
        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(ScanOperationStatuses.Cancelled, operations.Get("op-2").Status);
        Assert.False(Directory.Exists(ScanProcessor.BuildFinalDirectoryPreview(
            destinationRoot, "B31010", "1", true, ScanKinds.Original, null)));
    }

    [Fact]
    public void CancellationIsRefusedAfterFileProcessingStarts()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var config = workspace.CreateConfig(sourceRoot, destinationRoot);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        var store = new ScanOperationStore(Path.Combine(workspace.Root, "state"), logger);
        using var operations = new ScanOperationService(
            store,
            new ScanProcessor(config, new OperationStore()),
            new ScanWatchService(new AgentConfigProvider(config)),
            logger);
        store.Enqueue(workspace.CreateRequest());
        store.Update("op-1", current => current with
        {
            Status = ScanOperationStatuses.Processing,
            Phase = ScanOperationPhases.Copying
        });

        Assert.Null(operations.Cancel("op-1"));
        Assert.Equal(ScanOperationStatuses.Processing, operations.Get("op-1").Status);
    }

    [Fact]
    public async Task PersistedWatchingOperationResumesAfterAgentRestart()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var rollFolder = Path.Combine(sourceRoot, "roll-current");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch,
                    SettleStableSeconds = 0,
                    SettleTimeoutSeconds = 5,
                    WatchTimeoutSeconds = 10,
                    SettlePollSeconds = 1
                }
            ]
        };
        var configProvider = new AgentConfigProvider(config);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        var stateDir = Path.Combine(workspace.Root, "state");
        var persistedStore = new ScanOperationStore(stateDir, logger);
        persistedStore.Enqueue(workspace.CreateRequest() with { WaitForReady = true });
        persistedStore.Update("op-1", operation => operation with
        {
            Status = ScanOperationStatuses.Processing,
            Phase = ScanOperationPhases.Watching,
            StartedAt = DateTimeOffset.UtcNow
        });

        using var recovered = new ScanOperationService(
            new ScanOperationStore(stateDir, logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger);
        await Task.Delay(500);
        workspace.WriteFile(rollFolder, FileSystemSafety.ExportSentinelFileName, "{}");

        var completed = await WaitForTerminal(recovered, "op-1");

        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(1, completed.Result?.ImageCount);
    }

    [Fact]
    public async Task DurablePollingOperationSettlesExistingNewestFolderAndIncludesLateFiles()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var rollFolder = Path.Combine(sourceRoot, "roll-current");
        workspace.WriteFile(rollFolder, "frame001.tif", "image-one");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierPollingWatch,
                    SettleStableSeconds = 2,
                    SettleTimeoutSeconds = 8,
                    WatchTimeoutSeconds = 10,
                    SettlePollSeconds = 1
                }
            ]
        };
        var configProvider = new AgentConfigProvider(config);
        var logger = new LocalAgentLogger(Path.Combine(workspace.Root, "logs"));
        using var operations = new ScanOperationService(
            new ScanOperationStore(Path.Combine(workspace.Root, "state"), logger),
            new ScanProcessor(configProvider, new OperationStore()),
            new ScanWatchService(configProvider),
            logger);

        operations.Enqueue(workspace.CreateRequest() with { WaitForReady = true });
        await Task.Delay(1200);
        workspace.WriteFile(rollFolder, "frame002.tif", "image-two");

        var completed = await WaitForTerminal(operations, "op-1", timeoutSeconds: 15);

        Assert.Equal(ScanOperationStatuses.Completed, completed.Status);
        Assert.Equal(2, completed.Result?.ImageCount);
    }

    [Fact]
    public void SentinelCandidatesExposeReadinessWithoutHidingIncompleteFolders()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var waitFolder = Path.Combine(sourceRoot, "roll-WAIT");
        var readyFolder = Path.Combine(sourceRoot, "roll-Ready");
        workspace.WriteFile(waitFolder, "frame001.tif", "image-one");
        workspace.WriteFile(readyFolder, "frame001.tif", "image-two");
        workspace.WriteFile(readyFolder, FileSystemSafety.ExportSentinelFileName, "{}");
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierSentinelWatch
                }
            ]
        };

        var candidates = new SourceCandidateService(new AgentConfigProvider(config)).GetCandidates("dev-profile", null);

        var waiting = Assert.Single(candidates, candidate => candidate.Path == waitFolder);
        var ready = Assert.Single(candidates, candidate => candidate.Path == readyFolder);
        Assert.False(waiting.HasSentinel);
        Assert.False(waiting.ReadyForSelection);
        Assert.True(ready.HasSentinel);
        Assert.True(ready.ReadyForSelection);
    }

    [Fact]
    public async Task PollingWatchFailsClosedWhenFilesDoNotStabilize()
    {
        using var workspace = new TempWorkspace();
        var sourceRoot = workspace.CreateSource("Frontier");
        var destinationRoot = workspace.CreateDestination();
        var config = workspace.CreateConfig(sourceRoot, destinationRoot) with
        {
            Profiles =
            [
                workspace.CreateProfile(sourceRoot, destinationRoot) with
                {
                    ScannerMode = ScannerModes.FrontierPollingWatch,
                    SettleStableSeconds = 30,
                    SettleTimeoutSeconds = 1,
                    WatchTimeoutSeconds = 10,
                    SettlePollSeconds = 1
                }
            ]
        };
        var service = new ScanWatchService(new AgentConfigProvider(config));
        var watch = service.Start(new StartScanWatchRequest
        {
            ProfileId = "dev-profile",
            OrderNumber = "B31009",
            RollNumber = "1"
        });
        workspace.WriteFile(Path.Combine(sourceRoot, "roll-a"), "frame001.bmp", "still-being-written");

        ScanWatchState state;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        do
        {
            await Task.Delay(250);
            state = service.Get(watch.WatchId);
        } while (state.Status is not "error" && DateTimeOffset.UtcNow < deadline);

        Assert.Equal("error", state.Status);
        Assert.Contains("Nothing was processed", state.Message);
    }

    private static async Task<ScanWatchState> WaitForReady(ScanWatchService service, string watchId)
    {
        ScanWatchState state;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(250);
            state = service.Get(watchId);
        } while (state.Status is not "ready" && DateTimeOffset.UtcNow < deadline);

        return state;
    }

    private static async Task<ScanOperationState> WaitForTerminal(
        ScanOperationService service,
        string idempotencyKey,
        int timeoutSeconds = 10)
    {
        ScanOperationState state;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        do
        {
            await Task.Delay(100);
            state = service.Get(idempotencyKey);
        } while (!ScanOperationStatuses.IsTerminal(state.Status) && DateTimeOffset.UtcNow < deadline);

        return state;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"twincheck-agent-tests-{Guid.NewGuid():N}");

        public TempWorkspace()
        {
            Directory.CreateDirectory(Root);
        }

        public string CreateSource(string name)
        {
            var path = Path.Combine(Root, "sources", name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateDestination()
        {
            var path = Path.Combine(Root, "destination");
            Directory.CreateDirectory(path);
            return path;
        }

        public string FinalDir(string destinationRoot) =>
            Path.Combine(destinationRoot, ScannerFileSystem.GetWeekFolder(), "B31009", "B31009-1");

        public void WriteFile(string directory, string fileName, string contents)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), contents);
        }

        public ScannerProfile CreateProfile(string sourceDir, string destinationRoot) =>
            new()
            {
                Id = "dev-profile",
                Name = "Dev Profile",
                SourceDir = sourceDir,
                DestinationDir = destinationRoot
            };

        public AgentConfig CreateConfig(string sourceDir, string destinationRoot) =>
            new()
            {
                ApiKey = "test-key",
                AllowedSourceRoots = [Path.Combine(Root, "sources")],
                AllowedDestinationRoots = [destinationRoot],
                ActiveProfileId = "dev-profile",
                Profiles = [CreateProfile(sourceDir, destinationRoot)]
            };

        public ScanProcessor CreateProcessor(string sourceDir, string destinationRoot) =>
            new(CreateConfig(sourceDir, destinationRoot), new OperationStore());

        public ProcessScanRequest CreateRequest(bool dryRun = false, string? sourceOverride = null, string scanKind = ScanKinds.Original, int? rescanNumber = null) =>
            new()
            {
                IdempotencyKey = "op-1",
                ProfileId = "dev-profile",
                SourceDir = sourceOverride,
                OrderNumber = "B31009",
                RollNumber = "1",
                ScanKind = scanKind,
                RescanNumber = rescanNumber,
                DryRun = dryRun
            };

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
