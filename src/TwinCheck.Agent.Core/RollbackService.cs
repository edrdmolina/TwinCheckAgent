namespace TwinCheck.Agent.Core;

public sealed class RollbackService(AgentConfigProvider configProvider, OperationStore operationStore)
{
    public RollbackScanResult Rollback(RollbackScanRequest request)
    {
        var config = configProvider.Current;
        var profile = config.Profiles.SingleOrDefault(profile => profile.Id == request.ProfileId)
            ?? throw new InvalidOperationException($"Unknown scanner profile '{request.ProfileId}'.");
        var manifestPath = FileSystemSafety.EnsureInsideAnyRoot(
            request.ManifestPath,
            config.AllowedDestinationRoots,
            "manifest");
        var manifest = operationStore.ReadManifest(manifestPath);
        if (manifest.RolledBackAt is not null)
        {
            return new RollbackScanResult(false, manifestPath, "", manifest.FinalDir, 0, ["Manifest is already rolled back."]);
        }

        var finalDir = FileSystemSafety.EnsureInsideAnyRoot(manifest.FinalDir, config.AllowedDestinationRoots, "destination");
        var archiveDir = ResolveSourceArchiveDir(profile, manifest);
        Directory.CreateDirectory(archiveDir);

        var restored = 0;
        var warnings = new List<string>();
        foreach (var file in manifest.Files.Where(file => file.Outcome is ScanFileOutcome.Copied or ScanFileOutcome.ConflictRenamed or ScanFileOutcome.MovedToReview))
        {
            if (string.IsNullOrWhiteSpace(file.DestinationPath))
            {
                continue;
            }

            if (!File.Exists(file.DestinationPath))
            {
                warnings.Add($"Destination file is missing and could not be rolled back: {file.DestinationPath}");
                continue;
            }

            var restorePath = Path.Combine(archiveDir, file.FileName);
            restorePath = ResolveRollbackPath(restorePath);
            File.Move(file.DestinationPath, restorePath, overwrite: false);
            restored++;
        }

        TryDeleteEmptyDirectory(finalDir);
        operationStore.WriteManifestAt(manifestPath, manifest with { RolledBackAt = DateTimeOffset.UtcNow });

        return new RollbackScanResult(true, manifestPath, archiveDir, finalDir, restored, warnings);
    }

    private static string ResolveSourceArchiveDir(ScannerProfile profile, OperationManifest manifest)
    {
        var processedRoot = Path.Combine(profile.DestinationDir, "_processed");
        var archived = Directory.Exists(processedRoot)
            ? Directory.EnumerateDirectories(processedRoot)
                .FirstOrDefault(path => Path.GetFileName(path).Contains(manifest.IdempotencyKey, StringComparison.OrdinalIgnoreCase))
            : null;

        return archived ?? Path.Combine(processedRoot, $"rollback-{manifest.OrderNumber}-{manifest.RollNumber}-{manifest.IdempotencyKey}");
    }

    private static string ResolveRollbackPath(string restorePath)
    {
        if (!File.Exists(restorePath))
        {
            return restorePath;
        }

        var directory = Path.GetDirectoryName(restorePath)!;
        var name = Path.GetFileNameWithoutExtension(restorePath);
        var extension = Path.GetExtension(restorePath);
        for (var version = 2; version < 1000; version++)
        {
            var candidate = Path.Combine(directory, $"{name}-rollback-{version}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not resolve rollback destination for '{restorePath}'.");
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
