using System.Security.Cryptography;

namespace TwinCheck.Agent.Core;

public static class FileSystemSafety
{
    public const string ExportSentinelFileName = "export.done";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".tif",
        ".tiff",
        ".bmp"
    };

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsIgnoredControlFile(string path) =>
        string.Equals(Path.GetFileName(path), ExportSentinelFileName, StringComparison.OrdinalIgnoreCase);

    public static string EnsureInsideAnyRoot(string path, IReadOnlyCollection<string> roots, string label)
    {
        if (roots.Count == 0)
        {
            throw new InvalidOperationException($"No allowed {label} roots are configured.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!roots.Any(root => IsUnderRoot(fullPath, root)))
        {
            throw new InvalidOperationException($"{label} path is outside the configured allowed roots: {fullPath}");
        }

        return fullPath;
    }

    public static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return string.Equals(fullPath, fullRoot, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void CopyAndVerify(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);

        var sourceInfo = new FileInfo(sourcePath);
        var destinationInfo = new FileInfo(destinationPath);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            throw new IOException($"Copy length mismatch for '{sourcePath}'.");
        }

        var sourceHash = ComputeSha256(sourcePath);
        var destinationHash = ComputeSha256(destinationPath);
        if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Copy checksum mismatch for '{sourcePath}'.");
        }
    }

    public static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testPath = Path.Combine(directory, $".twincheck-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(testPath, "ok");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
