using System.Text.Json;
using ImageMagick;
using TwinCheck.Agent.Core;
using TwinCheck.Agent.Gui.ViewModels;
using TwinCheck.Agent.Imaging;

namespace TwinCheck.Agent.Tests;

public sealed class BmpToTiffTests
{
    [Fact]
    public void EnabledProfileConvertsBmpToVerifiedUncompressedTiffAndArchivesOriginal()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        var sourcePath = workspace.WriteImage(sourceDir, "scan001.bmp", MagickFormat.Bmp, MagickColors.Crimson);
        var sourceHash = FileSystemSafety.ComputeSha256(sourcePath);
        var sourceSize = new FileInfo(sourcePath).Length;
        var processor = workspace.CreateProcessor(sourceDir, destinationRoot, convertBmp: true);

        var result = processor.Process(workspace.CreateRequest());

        var outputPath = Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-1.tif");
        var archivedBmp = Path.Combine(result.Manifest.SourceArchiveDir!, "scan001.bmp");
        Assert.True(File.Exists(outputPath));
        Assert.False(File.Exists(Path.ChangeExtension(outputPath, ".bmp")));
        Assert.True(File.Exists(archivedBmp));
        Assert.Equal(sourceHash, FileSystemSafety.ComputeSha256(archivedBmp));

        using var original = new MagickImage(archivedBmp);
        using var converted = new MagickImage(outputPath);
        Assert.Equal(MagickFormat.Tiff, converted.Format);
        Assert.Equal(CompressionMethod.NoCompression, converted.Compression);
        Assert.Equal(original.Width, converted.Width);
        Assert.Equal(original.Height, converted.Height);
        Assert.Equal(0, original.Compare(converted, ErrorMetric.Absolute));
        Assert.Equal(original.Density.Units, converted.Density.Units);
        Assert.InRange(Math.Abs(original.Density.X - converted.Density.X), 0, 0.01);
        Assert.InRange(Math.Abs(original.Density.Y - converted.Density.Y), 0, 0.01);

        var file = Assert.Single(result.Manifest.Files);
        Assert.Equal(sourceSize, file.SourceSize);
        Assert.Equal(sourceHash, file.SourceSha256);
        Assert.Equal(new FileInfo(outputPath).Length, file.Size);
        Assert.Equal(FileSystemSafety.ComputeSha256(outputPath), file.Sha256);
        Assert.Equal(ScanFileConversions.BmpToTiff, file.Conversion);
        Assert.True(result.Manifest.EffectiveOptions.BmpToTiff);
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, ".twincheck-staging")));
    }

    [Fact]
    public void DisabledProfileLeavesBmpByteForByteUnchanged()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        var sourcePath = workspace.WriteImage(sourceDir, "scan001.bmp", MagickFormat.Bmp, MagickColors.Navy);
        var sourceHash = FileSystemSafety.ComputeSha256(sourcePath);
        var processor = workspace.CreateProcessor(sourceDir, destinationRoot, convertBmp: false);

        var result = processor.Process(workspace.CreateRequest());

        var outputPath = Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-1.bmp");
        Assert.True(File.Exists(outputPath));
        Assert.Equal(sourceHash, FileSystemSafety.ComputeSha256(outputPath));
        var file = Assert.Single(result.Manifest.Files);
        Assert.Null(file.Conversion);
        Assert.False(result.Manifest.EffectiveOptions.BmpToTiff);
    }

    [Fact]
    public void RequestOptionsOverrideProfileInBothDirections()
    {
        using var enabledWorkspace = new ImageWorkspace();
        var enabledSource = enabledWorkspace.CreateSource("roll-a");
        var enabledDestination = enabledWorkspace.CreateDestination();
        enabledWorkspace.WriteImage(enabledSource, "scan001.bmp", MagickFormat.Bmp, MagickColors.Green);
        var disabledProfileProcessor = enabledWorkspace.CreateProcessor(enabledSource, enabledDestination, convertBmp: false);

        var enabledResult = disabledProfileProcessor.Process(enabledWorkspace.CreateRequest(
            options: new ScanOptions { BmpToTiff = true }));

        Assert.EndsWith(".tif", Assert.Single(enabledResult.Manifest.Files).DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(enabledResult.Manifest.EffectiveOptions.BmpToTiff);

        using var disabledWorkspace = new ImageWorkspace();
        var disabledSource = disabledWorkspace.CreateSource("roll-b");
        var disabledDestination = disabledWorkspace.CreateDestination();
        disabledWorkspace.WriteImage(disabledSource, "scan001.bmp", MagickFormat.Bmp, MagickColors.Green);
        var enabledProfileProcessor = disabledWorkspace.CreateProcessor(disabledSource, disabledDestination, convertBmp: true);

        var disabledResult = enabledProfileProcessor.Process(disabledWorkspace.CreateRequest(
            options: new ScanOptions { BmpToTiff = false }));

        Assert.EndsWith(".bmp", Assert.Single(disabledResult.Manifest.Files).DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(disabledResult.Manifest.EffectiveOptions.BmpToTiff);
    }

    [Fact]
    public void MixedInputsConvertOnlyBmpAndKeepNaturalImageNumbering()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteImage(sourceDir, "scan10.bmp", MagickFormat.Bmp, MagickColors.Red);
        var pngPath = workspace.WriteImage(sourceDir, "scan3.png", MagickFormat.Png, MagickColors.Blue);
        var pngHash = FileSystemSafety.ComputeSha256(pngPath);
        workspace.WriteImage(sourceDir, "scan2.BMP", MagickFormat.Bmp, MagickColors.Green);
        var processor = workspace.CreateProcessor(sourceDir, destinationRoot, convertBmp: true);

        var result = processor.Process(workspace.CreateRequest());

        var finalDir = workspace.FinalDir(destinationRoot);
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-1.tif")));
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-2.png")));
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-3.tif")));
        Assert.Equal(pngHash, FileSystemSafety.ComputeSha256(Path.Combine(finalDir, "B31009-1-2.png")));
        Assert.Equal(2, result.Manifest.Files.Count(file => file.Conversion == ScanFileConversions.BmpToTiff));
    }

    [Fact]
    public void DryRunPlansVerifiedTiffWithoutPublishingOrArchiving()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteImage(sourceDir, "scan001.bmp", MagickFormat.Bmp, MagickColors.Orange);
        var processor = workspace.CreateProcessor(sourceDir, destinationRoot, convertBmp: true);

        var result = processor.Process(workspace.CreateRequest(dryRun: true));

        var file = Assert.Single(result.Manifest.Files);
        Assert.EndsWith(".tif", file.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScanFileConversions.BmpToTiff, file.Conversion);
        Assert.NotNull(file.Sha256);
        Assert.True(file.Size > 0);
        Assert.True(Directory.Exists(sourceDir));
        Assert.False(Directory.Exists(workspace.FinalDir(destinationRoot)));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "_processed")));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, ".twincheck-staging")));
    }

    [Fact]
    public void CorruptBmpFailsClosedAndCleansStaging()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        File.WriteAllText(Path.Combine(sourceDir, "scan001.bmp"), "not-a-bitmap");
        var processor = workspace.CreateProcessor(sourceDir, destinationRoot, convertBmp: true);

        Assert.Throws<InvalidDataException>(() => processor.Process(workspace.CreateRequest()));

        Assert.True(Directory.Exists(sourceDir));
        Assert.True(File.Exists(Path.Combine(sourceDir, "scan001.bmp")));
        Assert.Empty(Directory.EnumerateFiles(destinationRoot, "*.tif", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, "_processed")));
        Assert.False(Directory.Exists(Path.Combine(destinationRoot, ".twincheck-staging")));
        var manifestPath = Path.Combine(workspace.FinalDir(destinationRoot), "manifest-op-1.json");
        var failedManifest = new OperationStore().ReadManifest(manifestPath);
        Assert.Contains(failedManifest.Files, file => file.Outcome == ScanFileOutcome.Failed);
    }

    [Fact]
    public void ReprocessingSameBmpRecognizesIdenticalTiff()
    {
        using var workspace = new ImageWorkspace();
        var destinationRoot = workspace.CreateDestination();
        var firstSource = workspace.CreateSource("roll-a");
        var firstBmp = workspace.WriteImage(firstSource, "scan001.bmp", MagickFormat.Bmp, MagickColors.Purple);
        var originalBytes = File.ReadAllBytes(firstBmp);
        var firstProcessor = workspace.CreateProcessor(firstSource, destinationRoot, convertBmp: true);
        firstProcessor.Process(workspace.CreateRequest(idempotencyKey: "op-1"));

        var secondSource = workspace.CreateSource("roll-b");
        File.WriteAllBytes(Path.Combine(secondSource, "scan001.bmp"), originalBytes);
        var secondProcessor = workspace.CreateProcessor(secondSource, destinationRoot, convertBmp: true);
        var result = secondProcessor.Process(workspace.CreateRequest(idempotencyKey: "op-2"));

        Assert.Contains(result.Manifest.Files, file => file.Outcome == ScanFileOutcome.AlreadyDone);
        Assert.False(File.Exists(Path.Combine(workspace.FinalDir(destinationRoot), "B31009-1-1-v2.tif")));
    }

    [Fact]
    public void ExistingDifferentTiffUsesVersionedDestination()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteImage(sourceDir, "scan001.bmp", MagickFormat.Bmp, MagickColors.Black);
        var finalDir = workspace.FinalDir(destinationRoot);
        Directory.CreateDirectory(finalDir);
        workspace.WriteImage(finalDir, "B31009-1-1.tif", MagickFormat.Tiff, MagickColors.White);
        var processor = workspace.CreateProcessor(sourceDir, destinationRoot, convertBmp: true);

        var result = processor.Process(workspace.CreateRequest());

        Assert.Single(result.Conflicts);
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-1.tif")));
        Assert.True(File.Exists(Path.Combine(finalDir, "B31009-1-1-v2.tif")));
    }

    [Fact]
    public void RollbackKeepsConvertedArtifactTiffExtensionBesideOriginalBmp()
    {
        using var workspace = new ImageWorkspace();
        var sourceDir = workspace.CreateSource("roll-a");
        var destinationRoot = workspace.CreateDestination();
        workspace.WriteImage(sourceDir, "scan001.bmp", MagickFormat.Bmp, MagickColors.Yellow);
        var config = workspace.CreateConfig(sourceDir, destinationRoot, convertBmp: true);
        var operationStore = new OperationStore();
        var processor = new ScanProcessor(config, operationStore, new MagickScanImageConverter());
        var result = processor.Process(workspace.CreateRequest());
        var manifestPath = Path.Combine(result.Manifest.FinalDir, "manifest-op-1.json");

        var rollback = new RollbackService(new AgentConfigProvider(config), operationStore).Rollback(new RollbackScanRequest
        {
            ProfileId = "dev-profile",
            ManifestPath = manifestPath
        });

        Assert.True(rollback.Ok);
        Assert.True(File.Exists(Path.Combine(rollback.SourceArchiveDir, "scan001.bmp")));
        Assert.True(File.Exists(Path.Combine(rollback.SourceArchiveDir, "B31009-1-1.tif")));
        Assert.False(File.Exists(Path.Combine(result.Manifest.FinalDir, "B31009-1-1.tif")));
    }

    [Fact]
    public void ProfileEditorRoundTripsConversionAndPreservesExifOptions()
    {
        var profile = new ScannerProfile
        {
            Id = "scanner-1",
            Name = "Scanner 1",
            SourceDir = Path.GetTempPath(),
            DestinationDir = Path.GetTempPath(),
            Options = new ScanOptions
            {
                BmpToTiff = true,
                Exif = new ExifOptions { Artist = "Coastal Film Lab", Make = "Noritsu", Model = "HS-1800" }
            }
        };

        var editor = ProfileEditor.FromProfile(profile);
        editor.BmpToTiff = false;
        var saved = editor.ToProfile();

        Assert.False(saved.Options.BmpToTiff);
        Assert.Equal("Coastal Film Lab", saved.Options.Exif?.Artist);
        Assert.Equal("Noritsu", saved.Options.Exif?.Make);
        Assert.Equal("HS-1800", saved.Options.Exif?.Model);
    }

    [Fact]
    public void MissingOptionsInSavedProfileDefaultsConversionOff()
    {
        const string json = """
            {
              "id": "legacy",
              "name": "Legacy",
              "sourceDir": "/tmp/source",
              "destinationDir": "/tmp/destination"
            }
            """;

        var profile = JsonSerializer.Deserialize<ScannerProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(profile);
        Assert.False(profile.Options.BmpToTiff);
    }

    [Fact]
    public void LegacyManifestWithoutConversionFieldsStillLoads()
    {
        using var workspace = new ImageWorkspace();
        var manifestPath = Path.Combine(workspace.Root, "legacy-manifest.json");
        File.WriteAllText(manifestPath, """
            {
              "idempotencyKey": "legacy-op",
              "profileId": "legacy-profile",
              "orderNumber": "B31009",
              "rollNumber": "1",
              "sourceDir": "/tmp/source",
              "destinationDir": "/tmp/destination",
              "finalDir": "/tmp/destination/B31009-1",
              "dryRun": false,
              "ok": true,
              "startedAt": "2026-08-15T12:00:00Z",
              "files": [],
              "warnings": []
            }
            """);

        var manifest = new OperationStore().ReadManifest(manifestPath);

        Assert.False(manifest.EffectiveOptions.BmpToTiff);
        Assert.Empty(manifest.Files);
    }

    private sealed class ImageWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"twincheck-agent-image-tests-{Guid.NewGuid():N}");

        public ImageWorkspace() => Directory.CreateDirectory(Root);

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

        public string WriteImage(string directory, string fileName, MagickFormat format, IMagickColor<ushort> color)
        {
            var path = Path.Combine(directory, fileName);
            using var image = new MagickImage(color, 7, 5)
            {
                Format = format,
                Density = new Density(300, 300, DensityUnit.PixelsPerInch)
            };
            image.Write(path);
            return path;
        }

        public string FinalDir(string destinationRoot) =>
            Path.Combine(destinationRoot, ScannerFileSystem.GetWeekFolder(), "B31009", "B31009-1");

        public ScannerProfile CreateProfile(string sourceDir, string destinationRoot, bool convertBmp) =>
            new()
            {
                Id = "dev-profile",
                Name = "Dev Profile",
                SourceDir = sourceDir,
                DestinationDir = destinationRoot,
                Options = new ScanOptions { BmpToTiff = convertBmp }
            };

        public AgentConfig CreateConfig(string sourceDir, string destinationRoot, bool convertBmp) =>
            new()
            {
                ApiKey = "test-key",
                AllowedSourceRoots = [Path.Combine(Root, "sources")],
                AllowedDestinationRoots = [destinationRoot],
                ActiveProfileId = "dev-profile",
                Profiles = [CreateProfile(sourceDir, destinationRoot, convertBmp)]
            };

        public ScanProcessor CreateProcessor(string sourceDir, string destinationRoot, bool convertBmp) =>
            new(CreateConfig(sourceDir, destinationRoot, convertBmp), new OperationStore(), new MagickScanImageConverter());

        public ProcessScanRequest CreateRequest(
            bool dryRun = false,
            ScanOptions? options = null,
            string idempotencyKey = "op-1") =>
            new()
            {
                IdempotencyKey = idempotencyKey,
                ProfileId = "dev-profile",
                OrderNumber = "B31009",
                RollNumber = "1",
                Options = options,
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
