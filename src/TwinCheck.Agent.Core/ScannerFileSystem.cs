using System.Text.RegularExpressions;

namespace TwinCheck.Agent.Core;

public static partial class ScannerFileSystem
{
    public static string GetWeekFolder(DateTime? date = null)
    {
        var value = (date ?? DateTime.Today).Date;
        var monday = value.AddDays(-(int)value.DayOfWeek + (int)DayOfWeek.Monday);
        if (value.DayOfWeek == DayOfWeek.Sunday)
        {
            monday = value.AddDays(-6);
        }

        return monday.ToString("MM-dd-yy");
    }

    public static string GetNoritsuDailyFolder(string sourceRoot, DateTime? date = null)
    {
        var today = (date ?? DateTime.Today).ToString("yyyyMMdd");
        return Path.Combine(sourceRoot, today);
    }

    public static int CountImageFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Count(FileSystemSafety.IsImageFile)
            : 0;

    public static DateTimeOffset GetNewestImageModifiedAt(string directory)
    {
        var newestImage = Directory.EnumerateFiles(directory)
            .Where(FileSystemSafety.IsImageFile)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(directory))
            .Max();

        return new DateTimeOffset(newestImage, TimeSpan.Zero);
    }

    public static string[] GetImageFilesNatural(string directory) =>
        Directory.EnumerateFiles(directory)
            .Where(FileSystemSafety.IsImageFile)
            .OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance)
            .ToArray();

    public static string[] GetFilesNatural(string directory) =>
        Directory.EnumerateFiles(directory)
            .OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance)
            .ToArray();

    public static async Task<bool> WaitUntilStable(
        string targetDir,
        int stableSeconds,
        int timeoutSeconds,
        int pollSeconds,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        DateTimeOffset? stableSince = null;
        Dictionary<string, (long Length, DateTime LastWriteUtc)>? lastSignature = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = BuildSignature(targetDir);
            if (signature.Count > 0 && SignaturesEqual(signature, lastSignature) && AllOpenable(signature.Keys))
            {
                stableSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - stableSince >= TimeSpan.FromSeconds(Math.Max(0, stableSeconds)))
                {
                    return true;
                }
            }
            else
            {
                stableSince = null;
                lastSignature = signature;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                onStatus?.Invoke("Warning: settle timeout reached; proceeding anyway.");
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, pollSeconds)), cancellationToken);
        }
    }

    private static Dictionary<string, (long Length, DateTime LastWriteUtc)> BuildSignature(string targetDir)
    {
        var signature = new Dictionary<string, (long Length, DateTime LastWriteUtc)>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.EnumerateFiles(targetDir))
        {
            try
            {
                var info = new FileInfo(filePath);
                signature[filePath] = (info.Length, info.LastWriteTimeUtc);
            }
            catch (IOException)
            {
                signature[filePath] = (-1, DateTime.MinValue);
            }
            catch (UnauthorizedAccessException)
            {
                signature[filePath] = (-1, DateTime.MinValue);
            }
        }

        return signature;
    }

    private static bool SignaturesEqual(
        Dictionary<string, (long Length, DateTime LastWriteUtc)> current,
        Dictionary<string, (long Length, DateTime LastWriteUtc)>? previous)
    {
        if (previous is null || current.Count != previous.Count)
        {
            return false;
        }

        return current.All(pair => previous.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static bool AllOpenable(IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    private sealed partial class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }

            var xParts = NaturalPartRegex().Matches(x);
            var yParts = NaturalPartRegex().Matches(y);
            var count = Math.Min(xParts.Count, yParts.Count);
            for (var index = 0; index < count; index++)
            {
                var xPart = xParts[index].Value;
                var yPart = yParts[index].Value;
                var xIsNumeric = long.TryParse(xPart, out var xNumber);
                var yIsNumeric = long.TryParse(yPart, out var yNumber);
                var bothNumeric = xIsNumeric && yIsNumeric;
                var result = bothNumeric
                    ? xNumber.CompareTo(yNumber)
                    : string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                if (result != 0)
                {
                    return result;
                }
            }

            return xParts.Count.CompareTo(yParts.Count);
        }

        [GeneratedRegex(@"\d+|\D+")]
        private static partial Regex NaturalPartRegex();
    }
}
