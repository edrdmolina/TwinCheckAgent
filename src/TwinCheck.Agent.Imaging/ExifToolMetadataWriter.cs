using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Imaging;

/// <summary>Edits metadata without decoding/re-encoding image pixels, then verifies all requested tags.</summary>
public sealed class ExifToolMetadataWriter : IScanMetadataWriter
{
    public const string PathEnvironmentVariable = "TWINCHECK_EXIFTOOL_PATH";
    private readonly string? executable;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private const string Config = """
        %Image::ExifTool::UserDefined = (
            'Image::ExifTool::XMP::Main' => {
                twincheck => { SubDirectory => { TagTable => 'Image::ExifTool::UserDefined::twincheck' } },
            },
        );
        %Image::ExifTool::UserDefined::twincheck = (
            GROUPS => { 0 => 'XMP', 1 => 'XMP-twincheck', 2 => 'Image' },
            NAMESPACE => { twincheck => 'urn:twincheck:scan-metadata:1.0' },
            WRITABLE => 'string',
            FilmStock => { },
        );
        1;
        """;

    public ExifToolMetadataWriter() : this(FindExecutable()) { }
    public ExifToolMetadataWriter(string? executable) => this.executable = executable;
    public bool IsAvailable => executable is not null && File.Exists(executable);

    public void Write(string stagedPath, PhotoMetadata metadata)
    {
        if (!metadata.HasValues) return;
        if (!IsAvailable) throw new ScanMetadataWriteException("ExifTool is unavailable. Install it or set TWINCHECK_EXIFTOOL_PATH.");
        if (Path.GetExtension(stagedPath).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            throw new ScanMetadataWriteException("BMP cannot store the requested EXIF/XMP metadata. Enable BMP-to-TIFF conversion in the scanner profile.");

        var configPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stagedPath))!, $".exiftool-{Guid.NewGuid():N}.config");
        try
        {
            File.WriteAllText(configPath, Config, new UTF8Encoding(false));
            var arguments = BaseArguments(configPath);
            arguments.Add("-overwrite_original"); // Only the staged copy is changed; source originals are never passed here.
            if (metadata.Artist is { } artist)
            {
                // Deliberately retain Unicode in EXIF using MWG's UTF-8 convention, and standard UTF-8 XMP.
                arguments.Add($"-EXIF:Artist={artist}");
                arguments.Add($"-XMP-dc:Creator={artist}");
            }
            if (metadata.Make is { } make)
            {
                arguments.Add($"-EXIF:Make={make}");
                arguments.Add($"-XMP-tiff:Make={make}");
            }
            if (metadata.Model is { } model)
            {
                arguments.Add($"-EXIF:Model={model}");
                arguments.Add($"-XMP-tiff:Model={model}");
            }
            if (metadata.FilmStock is { } stock)
            {
                arguments.Add($"-XMP-twincheck:FilmStock={stock}");
                // Replace only this keyword if it already exists; preserve unrelated keywords.
                arguments.Add($"-XMP-dc:Subject-={stock}");
                arguments.Add($"-XMP-dc:Subject+={stock}");
            }
            arguments.Add(Path.GetFullPath(stagedPath));
            Run(arguments);

            var readArguments = BaseArguments(configPath);
            readArguments.AddRange(["-j", "-G1", "-EXIF:Artist", "-EXIF:Make", "-EXIF:Model",
                "-XMP-dc:Creator", "-XMP-dc:Subject", "-XMP-twincheck:FilmStock", "-XMP-tiff:Make", "-XMP-tiff:Model"]);
            readArguments.Add(Path.GetFullPath(stagedPath));
            using var document = JsonDocument.Parse(Run(readArguments));
            var tags = document.RootElement[0];
            RequireString(tags, "IFD0:Artist", metadata.Artist);
            RequireString(tags, "IFD0:Make", metadata.Make);
            RequireString(tags, "IFD0:Model", metadata.Model);
            RequireString(tags, "XMP-tiff:Make", metadata.Make);
            RequireString(tags, "XMP-tiff:Model", metadata.Model);
            RequireString(tags, "XMP-twincheck:FilmStock", metadata.FilmStock);
            RequireListContains(tags, "XMP-dc:Creator", metadata.Artist);
            RequireListContains(tags, "XMP-dc:Subject", metadata.FilmStock);
        }
        catch (ScanMetadataWriteException) { throw; }
        catch (Exception exception)
        {
            throw new ScanMetadataWriteException($"Photo metadata write/verification failed: {exception.Message}", exception);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    private static List<string> BaseArguments(string configPath) =>
        ["-config", configPath, "-charset", "UTF8", "-charset", "exif=UTF8", "-charset", "filename=UTF8"];

    private string Run(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new ScanMetadataWriteException("Could not start ExifTool.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            // A process stuck in a hung file write can't exit even when killed; don't wait for it forever.
            process.WaitForExit(KillWait);
            throw new ScanMetadataWriteException("ExifTool timed out while editing/verifying photo metadata.");
        }
        var stderr = error.GetAwaiter().GetResult();
        var stdout = output.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
            throw new ScanMetadataWriteException($"ExifTool failed (exit {process.ExitCode}): {stderr.Trim()}");
        return stdout;
    }

    private static void RequireString(JsonElement tags, string name, string? expected)
    {
        if (expected is null) return;
        if (!tags.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != expected)
            throw new ScanMetadataWriteException($"Metadata verification failed for {name}.");
    }

    private static void RequireListContains(JsonElement tags, string name, string? expected)
    {
        if (expected is null) return;
        if (!tags.TryGetProperty(name, out var value)
            || !(value.ValueKind == JsonValueKind.String && value.GetString() == expected
                || value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item => item.GetString() == expected)))
            throw new ScanMetadataWriteException($"Metadata verification failed for {name}.");
    }

    private static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var name = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "exiftool", name);
        if (File.Exists(bundled)) return bundled;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
