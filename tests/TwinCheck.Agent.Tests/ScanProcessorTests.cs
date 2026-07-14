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
            && candidate.ScannerMode == ScannerModes.FrontierFolder);
        Assert.Contains(candidates, candidate =>
            candidate.Name == "B31485-9"
            && candidate.Path == secondRollFolder
            && candidate.ImageCount == 1
            && !candidate.IsConfiguredRoot);
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
                    ScannerMode = ScannerModes.NoritsuDailyWatch,
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
