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

    public static bool IsIgnoredControlFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(fileName, ExportSentinelFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("._", StringComparison.Ordinal);
    }

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

    public static (long Length, string Sha256) CopyWithSha256(
        string sourcePath,
        string destinationPath,
        Action<long>? onBytesCopied = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var before = new FileInfo(sourcePath);
        var expectedLength = before.Length;
        var expectedLastWriteUtc = before.LastWriteTimeUtc;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            copied += read;
            onBytesCopied?.Invoke(read);
        }

        destination.Flush(flushToDisk: true);
        var after = new FileInfo(sourcePath);
        if (copied != expectedLength || after.Length != expectedLength || after.LastWriteTimeUtc != expectedLastWriteUtc)
        {
            throw new IOException($"Source changed while it was being copied: '{sourcePath}'.");
        }

        return (copied, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public static void VerifySha256(string path, long expectedLength, string expectedHash, Action<long>? onBytesRead = null)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedLength)
        {
            throw new IOException($"Copy length mismatch for '{path}'.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            onBytesRead?.Invoke(read);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Copy checksum mismatch for '{path}'.");
        }
    }

    public static void CopyAndVerify(string sourcePath, string destinationPath)
    {
        var copied = CopyWithSha256(sourcePath, destinationPath);
        VerifySha256(destinationPath, copied.Length, copied.Sha256);
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
