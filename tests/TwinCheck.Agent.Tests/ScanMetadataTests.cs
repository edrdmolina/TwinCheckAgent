using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ImageMagick;
using TwinCheck.Agent.Core;
using TwinCheck.Agent.Gui.ViewModels;
using TwinCheck.Agent.Imaging;

namespace TwinCheck.Agent.Tests;

public sealed class ScanMetadataTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), "twincheck-metadata", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyFingerprintFixtureIsUnchangedAndMatchesMigratedJob()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-job-fingerprint.json")));
        var request = fixture.RootElement.GetProperty("request").Deserialize<ProcessScanRequest>(Json)!;
        var store = new ScanOperationStore(Path.Combine(root, "db"));
        // Indented JSON uses the platform's line endings, so the recorded fixture (made on Linux) only matches
        // off Windows. Each station is consistent with itself, which is what migration and retries rely on.
        var expected = store.Enqueue(request).Operation.RequestFingerprint;
        if (!OperatingSystem.IsWindows())
            Assert.Equal(fixture.RootElement.GetProperty("requestFingerprint").GetString(), expected);
        // A previously saved record must survive migration and match a retry of reordered JSONB content.
        var stateDir = Path.Combine(root, "legacy");
        Directory.CreateDirectory(Path.Combine(stateDir, "operations"));
        var legacy = new ScanOperationState
        {
            IdempotencyKey = request.IdempotencyKey, OperationId = request.IdempotencyKey,
            Request = request, RequestFingerprint = expected, Status = ScanOperationStatuses.Cancelled,
            Phase = ScanOperationPhases.Cancelled, CompletedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(stateDir, "operations", "operation-legacy.json"), JsonSerializer.Serialize(legacy, Json));
        var migrated = new ScanOperationStore(stateDir);
        var retry = migrated.Enqueue(ReverseKeys(fixture.RootElement.GetProperty("request"))!.Deserialize<ProcessScanRequest>(Json)!);
        Assert.False(retry.Created);
        Assert.False(retry.Conflict);
        Assert.Equal(expected, retry.Operation.RequestFingerprint);
    }

    [Fact]
    public void ReorderedMetadataIsARetryAndDifferentMetadataConflicts()
    {
        var first = Request() with { Metadata = new ScanMetadata { CustomerName = "José Núñez", FilmStock = "Kodak Portra 400" } };
        var store = new ScanOperationStore(Path.Combine(root, "db"));
        var saved = store.Enqueue(first);
        var reordered = ReverseKeys(JsonSerializer.SerializeToElement(first, Json))!.Deserialize<ProcessScanRequest>(Json)!;
        var retry = store.Enqueue(reordered);
        Assert.False(retry.Created);
        Assert.False(retry.Conflict);
        Assert.Equal(saved.Operation.RequestFingerprint, retry.Operation.RequestFingerprint);
        Assert.True(store.Enqueue(first with { Metadata = first.Metadata! with { FilmStock = "Ilford HP5 Plus" } }).Conflict);
        Assert.True(store.Enqueue(first with { Metadata = first.Metadata! with { CustomerName = "Someone else" } }).Conflict);
        Assert.True(store.Enqueue(first with { Metadata = null }).Conflict);
        Assert.Single(store.ListActive());
    }

    [Theory]
    [InlineData(".jpg", false)]
    [InlineData(".tif", false)]
    [InlineData(".png", false)]
    [InlineData(".bmp", true)]
    public void ScanWritesMetadataWithoutChangingPixelsOrArchivedOriginal(string extension, bool conversion)
    {
        var source = CreateImage(extension);
        var sourceHash = FileSystemSafety.ComputeSha256(source);
        // Processing deletes the original once the upload is verified, so compare pixels with a copy.
        var reference = Path.Combine(root, "reference" + extension);
        File.Copy(source, reference);
        var profile = Profile() with { Options = new ScanOptions { BmpToTiff = conversion, Exif = new ExifOptions { Make = "Fujifilm", Model = "SP500", Artist = "Fallback artist" } } };
        var processor = Processor(profile, Writer());
        var result = processor.Process(Request() with
        {
            Metadata = new ScanMetadata { CustomerName = "José Núñez", FilmStock = "Kodak Portra 400" },
            // These must not override the local scanner identity or the job customer.
            Options = new ScanOptions { BmpToTiff = conversion, Exif = new ExifOptions { Make = "Injected Make", Model = "Injected Model", Artist = "Injected Artist" } }
        });
        var file = Assert.Single(result.Manifest.Files);
        Assert.False(File.Exists(source));
        Assert.Equal(sourceHash, file.SourceSha256);
        Assert.NotEqual(sourceHash, file.Sha256);
        // The delivered checksum must include the metadata edit and match the verified staged output.
        FileSystemSafety.VerifySha256(file.DestinationPath!, file.Size, file.Sha256!);
        using var original = new MagickImage(reference);
        using var delivered = new MagickImage(file.DestinationPath!);
        Assert.Equal(0, original.Compare(delivered, ErrorMetric.Absolute));
        // TIFF stores these native IFD tags rather than a separate EXIF profile blob.
        using var tags = ReadExif(file.DestinationPath!);
        Assert.Equal("José Núñez", tags.RootElement[0].GetProperty("IFD0:Artist").GetString());
        Assert.Equal("Fujifilm", tags.RootElement[0].GetProperty("IFD0:Make").GetString());
        Assert.Equal("SP500", tags.RootElement[0].GetProperty("IFD0:Model").GetString());
        var xmp = Encoding.UTF8.GetString(delivered.GetXmpProfile()!.ToByteArray()!);
        Assert.Contains("José Núñez", xmp);
        Assert.Contains("Kodak Portra 400", xmp);
        Assert.Contains(PhotoMetadata.NamespaceUri, xmp);
        Assert.Contains("twincheck:FilmStock", xmp);
        Assert.Contains("dc:subject", xmp);
        Assert.Contains("dc:creator", xmp);
        Assert.Equal("Fujifilm", result.Manifest.EffectiveOptions.Exif!.Make);
        Assert.Empty(Directory.GetFiles(profile.DestinationDir, "*_original", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "   ")]
    [InlineData("Jane Smith", null)]
    [InlineData(null, "Ilford HP5 Plus")]
    public void MissingValuesDoNotWriteEmptyOrPlaceholderTags(string? customer, string? stock)
    {
        var source = CreateImage(".jpg");
        var sourceHash = FileSystemSafety.ComputeSha256(source);
        var profile = Profile();
        var metadata = customer is null && stock is null ? null : new ScanMetadata { CustomerName = customer, FilmStock = stock };
        var result = Processor(profile, Writer()).Process(Request() with { Metadata = metadata });
        var output = Assert.Single(result.Manifest.Files).DestinationPath!;
        using var image = new MagickImage(output);
        var artist = image.GetExifProfile()?.GetValue(ExifTag.Artist)?.Value;
        Assert.Equal(string.IsNullOrWhiteSpace(customer) ? null : customer, artist);
        var xmp = image.GetXmpProfile()?.ToByteArray();
        if (string.IsNullOrWhiteSpace(customer) && string.IsNullOrWhiteSpace(stock))
        {
            Assert.Equal(sourceHash, FileSystemSafety.ComputeSha256(output));
            Assert.Null(xmp);
        }
        else
        {
            var xml = Encoding.UTF8.GetString(xmp!);
            Assert.Equal(!string.IsNullOrWhiteSpace(customer), xml.Contains("dc:creator"));
            Assert.Equal(!string.IsNullOrWhiteSpace(stock), xml.Contains("twincheck:FilmStock"));
        }
        Assert.Null(image.GetExifProfile()?.GetValue(ExifTag.Make));
        Assert.Null(image.GetExifProfile()?.GetValue(ExifTag.Model));
    }

    [Fact]
    public void WriterPreservesUnrelatedKeywordsAndDoesNotDuplicateFilmStock()
    {
        var file = CreateImage(".jpg");
        using (var image = new MagickImage(file))
        {
            image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes("""
                <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:subject><rdf:Bag>
                <rdf:li>holiday</rdf:li><rdf:li>Kodak Portra 400</rdf:li></rdf:Bag></dc:subject></rdf:Description>
                </rdf:RDF></x:xmpmeta>
                """)));
            image.Write(file);
        }
        var writer = Writer();
        var metadata = new PhotoMetadata(null, null, null, "Kodak Portra 400");
        writer.Write(file, metadata);
        writer.Write(file, metadata);
        using var delivered = new MagickImage(file);
        var xml = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(delivered.GetXmpProfile()!.ToByteArray()!));
        var dc = (System.Xml.Linq.XNamespace)"http://purl.org/dc/elements/1.1/";
        var rdf = (System.Xml.Linq.XNamespace)"http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var keywords = xml.Descendants(dc + "subject").SelectMany(subject => subject.Descendants(rdf + "li")).Select(item => item.Value).ToArray();
        Assert.Equal(["holiday", "Kodak Portra 400"], keywords);
    }

    [Fact]
    public async Task MetadataFailureFailsOperationWithoutArchivingOrDeliveringOutput()
    {
        var source = CreateImage(".jpg");
        var hash = FileSystemSafety.ComputeSha256(source);
        var profile = Profile();
        var config = Config(profile);
        var processor = Processor(profile, new FailingWriter());
        var logger = new LocalAgentLogger(Path.Combine(root, "logs"));
        var store = new ScanOperationStore(Path.Combine(root, "db"));
        using var operations = new ScanOperationService(store, processor, new ScanWatchService(new AgentConfigProvider(config)), logger);
        operations.Enqueue(Request() with { Metadata = new ScanMetadata { CustomerName = "Jane Smith" } });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!ScanOperationStatuses.IsTerminal(operations.Get("metadata-job").Status) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        var failed = operations.Get("metadata-job");
        Assert.Equal(ScanOperationStatuses.Failed, failed.Status);
        Assert.Equal("metadata-write-failed", failed.ErrorCode);
        Assert.Contains("intentional failure", failed.Error);
        Assert.Equal(hash, FileSystemSafety.ComputeSha256(source));
        Assert.Empty(Directory.GetFiles(profile.DestinationDir, "*.jpg", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(profile.SourceDir, "_processed-*"));
    }

    [Fact]
    public void MetadataOnUnconvertedBmpFailsExplicitly()
    {
        var source = CreateImage(".bmp");
        var hash = FileSystemSafety.ComputeSha256(source);
        var exception = Assert.Throws<ScanMetadataWriteException>(() => Processor(Profile(), Writer()).Process(Request() with { Metadata = new ScanMetadata { FilmStock = "Portra 400" } }));
        Assert.Contains("BMP-to-TIFF", exception.Message);
        Assert.Equal(hash, FileSystemSafety.ComputeSha256(source));
    }

    [Fact]
    public void ProfileSettingsRoundTripAndFallbackArtistIsLocal()
    {
        var editor = ProfileEditor.FromProfile(Profile());
        editor.Exif = new ExifOptions { Artist = "Local artist", Make = "Fujifilm", Model = "SP500" };
        var saved = editor.ToProfile();
        Assert.Equal(editor.Exif, saved.Options.Exif);
        Assert.Equal("Local artist", PhotoMetadata.Resolve(new ScanMetadata { CustomerName = " " }, saved.Options.Exif).Artist);
        Assert.Equal("Customer", PhotoMetadata.Resolve(new ScanMetadata { CustomerName = " Customer " }, saved.Options.Exif).Artist);
    }

    [Fact]
    public void CapabilityIsAdvertisedOnlyWhenMetadataWriterIsAvailable()
    {
        var config = Config(Profile());
        var available = new HealthService(new AgentConfigProvider(config), new OperationStore(), metadataWriter: Writer());
        Assert.Contains(PhotoMetadata.Capability, available.GetHealth("http://localhost").Capabilities);
        var missing = new HealthService(new AgentConfigProvider(config), new OperationStore(), metadataWriter: new ExifToolMetadataWriter(Path.Combine(root, "missing-exiftool")));
        Assert.DoesNotContain(PhotoMetadata.Capability, missing.GetHealth("http://localhost").Capabilities);
        Assert.Contains(missing.GetHealth("http://localhost").Warnings, message => message.Contains("ExifTool"));
    }

    private static ExifToolMetadataWriter Writer()
    {
        var writer = new ExifToolMetadataWriter();
        Assert.True(writer.IsAvailable, "ExifTool is required for metadata integration tests. Install it or set TWINCHECK_EXIFTOOL_PATH.");
        return writer;
    }

    private static JsonDocument ReadExif(string path)
    {
        var executable = Environment.GetEnvironmentVariable(ExifToolMetadataWriter.PathEnvironmentVariable)
            ?? (OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-j", "-G1", "-charset", "exif=UTF8", "-charset", "filename=UTF8", "-EXIF:Artist", "-EXIF:Make", "-EXIF:Model", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(error.GetAwaiter().GetResult());
        return JsonDocument.Parse(output.GetAwaiter().GetResult());
    }

    private string CreateImage(string extension)
    {
        // Rolls are always folders inside the watch folder.
        var source = Path.Combine(root, "source", "roll-1");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "scan001" + extension);
        using var image = new MagickImage(MagickColors.Crimson, 32, 24);
        image.Density = new Density(300, 300, DensityUnit.PixelsPerInch);
        image.Write(file);
        return file;
    }

    private ScannerProfile Profile() => new() { Id = "scanner", Name = "Scanner", SourceDir = Path.Combine(root, "source"), DestinationDir = Path.Combine(root, "output") };
    private AgentConfig Config(ScannerProfile profile) => new() { ApiKey = "test", Profiles = [profile], AllowedSourceRoots = [profile.SourceDir], AllowedDestinationRoots = [profile.DestinationDir], StagingDir = Path.Combine(root, "staging") };
    private ScanProcessor Processor(ScannerProfile profile, IScanMetadataWriter writer) => new(new AgentConfigProvider(Config(profile)), new OperationStore(), new MagickScanImageConverter(), writer);
    private static ProcessScanRequest Request() => new() { IdempotencyKey = "metadata-job", ProfileId = "scanner", OrderNumber = "B123", RollNumber = "1" };

    private static object? ReverseKeys(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Reverse().ToDictionary(property => property.Name, property => ReverseKeys(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ReverseKeys).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble(),
        _ => null
    };

    private sealed class FailingWriter : IScanMetadataWriter
    {
        public bool IsAvailable => true;
        public void Write(string stagedPath, PhotoMetadata metadata) => throw new IOException("intentional failure");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

internal static class MetadataTestJsonExtensions
{
    public static T? Deserialize<T>(this object value, JsonSerializerOptions options) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, options), options);
}
