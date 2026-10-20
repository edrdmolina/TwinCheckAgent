using System.Text.Json;

namespace TwinCheck.Agent.Core;

public static class CleanupKinds
{
    /// <summary>Originals kept by agent versions before the upload queue, in <c>&lt;destination&gt;/_processed/</c>.</summary>
    public const string LegacyArchive = "legacy-archive";
    /// <summary>A <c>_processed-…</c> roll folder kept after upload because it held items that were not uploaded.</summary>
    public const string KeptProcessed = "kept-processed";
}

public sealed record CleanupEntry(
    string Path,
    string Kind,
    string Name,
    long SizeBytes,
    int FileCount,
    DateTimeOffset ModifiedAt,
    bool Verified,
    string Reason,
    string? FinalDir);

public sealed record CleanupSkip(string Path, string Reason);

public sealed record CleanupDeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<CleanupSkip> Skipped);

/// <summary>
/// Lists folders of originals that are safe to delete because their roll's finished files are verified on
/// the destination, and deletes them on request.
/// </summary>
public sealed class CleanupService(
    AgentConfigProvider configProvider,
    ScanOperationStore store,
    DestinationWriteLock destinationWrites,
    LocalAgentLogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SkippedWhenSearching = new(StringComparer.OrdinalIgnoreCase)
    {
        "_processed", "_rolledback", ".twincheck-staging"
    };

    public IReadOnlyList<CleanupEntry> List()
    {
        var config = configProvider.Current;
        var uploads = store.ListUploads(finishedLimit: 500);
        var inQueue = uploads.Where(roll => !UploadStatuses.IsFinished(roll.Status))
            .Select(roll => Normalize(roll.SourceArchiveDir))
            .ToHashSet(PathComparer);
        var folders = FindFolders(config)
            .Where(folder => !inQueue.Contains(folder.Path))
            .ToArray();
        if (folders.Length == 0) return [];

        var manifests = new Dictionary<string, OperationManifest>(PathComparer);
        foreach (var roll in uploads.Where(roll => roll.ManifestWritten))
        {
            if (TryReadManifest(Path.Combine(roll.FinalDir, ManifestFileName(roll.IdempotencyKey))) is { } manifest)
            {
                manifests[Normalize(roll.SourceArchiveDir)] = manifest;
            }
        }

        if (folders.Any(folder => !manifests.ContainsKey(folder.Path)))
        {
            // Older rolls are only known from their manifests, so search the destinations once.
            foreach (var manifest in SearchManifests(DestinationRoots(config)))
            {
                if (!string.IsNullOrWhiteSpace(manifest.SourceArchiveDir))
                {
                    manifests.TryAdd(Normalize(manifest.SourceArchiveDir), manifest);
                }
            }
        }

        return folders
            .Select(folder =>
            {
                manifests.TryGetValue(folder.Path, out var manifest);
                var (verified, reason) = Verify(manifest);
                var (size, count) = Measure(folder.Path);
                return new CleanupEntry(
                    folder.Path,
                    folder.Kind,
                    System.IO.Path.GetFileName(folder.Path),
                    size,
                    count,
                    new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder.Path), TimeSpan.Zero),
                    verified,
                    reason,
                    manifest?.FinalDir);
            })
            .OrderByDescending(entry => entry.ModifiedAt)
            .ToArray();
    }

    /// <summary>Deletes the given folders after checking each one again; unverified folders are skipped.</summary>
    public CleanupDeleteResult Delete(IReadOnlyCollection<string> paths)
    {
        var entries = List().ToDictionary(entry => entry.Path, PathComparer);
        var deleted = new List<string>();
        var skipped = new List<CleanupSkip>();
        foreach (var requested in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(PathComparer))
        {
            var path = Normalize(requested);
            if (!entries.TryGetValue(path, out var entry))
            {
                skipped.Add(new CleanupSkip(requested, "Not a folder listed in Cleanup."));
                continue;
            }

            if (!entry.Verified)
            {
                skipped.Add(new CleanupSkip(requested, entry.Reason));
                continue;
            }

            try
            {
                destinationWrites.Run(() => Directory.Delete(entry.Path, recursive: true));
                deleted.Add(entry.Path);
                logger.Info($"Cleanup deleted {entry.Path} ({entry.FileCount} file(s), {entry.SizeBytes} bytes).");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new CleanupSkip(requested, $"Could not delete it: {exception.Message}"));
            }
        }

        return new CleanupDeleteResult(deleted, skipped);
    }

    private static IEnumerable<(string Path, string Kind)> FindFolders(AgentConfig config)
    {
        var seen = new HashSet<string>(PathComparer);
        foreach (var root in DestinationRoots(config))
        {
            var archiveRoot = System.IO.Path.Combine(root, "_processed");
            if (!Directory.Exists(archiveRoot)) continue;
            foreach (var folder in Directory.EnumerateDirectories(archiveRoot).Select(Normalize))
            {
                if (seen.Add(folder)) yield return (folder, CleanupKinds.LegacyArchive);
            }
        }

        foreach (var profile in config.Profiles)
        {
            var sourceRoot = Normalize(profile.SourceDir);
            if (!Directory.Exists(sourceRoot)) continue;
            // Noritsu rolls live in daily folders, so their _processed-… folders are one level deeper.
            var watchFolders = ScannerModes.Normalize(profile.ScannerMode) == ScannerModes.NoritsuWatch
                ? Directory.EnumerateDirectories(sourceRoot).Prepend(sourceRoot)
                : [sourceRoot];
            foreach (var watchFolder in watchFolders)
            {
                foreach (var folder in Directory.EnumerateDirectories(watchFolder)
                    .Where(FileSystemSafety.IsProcessedFolder)
                    .Select(Normalize))
                {
                    if (seen.Add(folder)) yield return (folder, CleanupKinds.KeptProcessed);
                }
            }
        }
    }

    private static (bool Verified, string Reason) Verify(OperationManifest? manifest)
    {
        if (manifest is null) return (false, "No finished roll refers to this folder, so its files can't be confirmed.");
        if (manifest.CompletedAt is null) return (false, "Its roll never finished.");
        if (manifest.RolledBackAt is not null) return (false, "Its roll was rolled back.");
        if (!manifest.Ok) return (false, "Its roll finished with errors.");

        var delivered = manifest.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.DestinationPath) && file.Outcome != ScanFileOutcome.Failed)
            .ToArray();
        foreach (var file in delivered)
        {
            var info = new FileInfo(file.DestinationPath!);
            if (!info.Exists) return (false, $"A finished file is missing: {file.DestinationPath}");
            if (info.Length != file.Size) return (false, $"A finished file has the wrong size: {file.DestinationPath}");
        }

        return (true, $"All {delivered.Length} finished file(s) are on the destination.");
    }

    private static (long Size, int Count) Measure(string folder)
    {
        long size = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            size += new FileInfo(file).Length;
            count++;
        }

        return (size, count);
    }

    private static IEnumerable<OperationManifest> SearchManifests(IEnumerable<string> roots)
    {
        var pending = new Stack<string>(roots.Where(Directory.Exists));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> manifestPaths;
            IEnumerable<string> children;
            try
            {
                manifestPaths = Directory.EnumerateFiles(directory, "manifest-*.json").ToArray();
                children = Directory.EnumerateDirectories(directory)
                    .Where(child => !SkippedWhenSearching.Contains(System.IO.Path.GetFileName(child))
                        && !FileSystemSafety.IsProcessedFolder(child))
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var manifestPath in manifestPaths)
            {
                if (TryReadManifest(manifestPath) is { } manifest) yield return manifest;
            }

            foreach (var child in children) pending.Push(child);
        }
    }

    private static OperationManifest? TryReadManifest(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> DestinationRoots(AgentConfig config) =>
        config.AllowedDestinationRoots
            .Concat(config.Profiles.Select(profile => profile.DestinationDir))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Normalize)
            .Distinct(PathComparer);

    private static string ManifestFileName(string idempotencyKey) => $"manifest-{ScanProcessor.SafePathToken(idempotencyKey)}.json";

    private static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
