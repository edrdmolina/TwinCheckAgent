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
        // Originals are deleted once a roll is uploaded, so rolled-back files go to their own folder,
        // outside every watch folder, where they can't be picked up as a new roll.
        var destinationRoot = FileSystemSafety.EnsureInsideAnyRoot(manifest.DestinationDir, config.AllowedDestinationRoots, "destination");
        var rollbackDir = ResolveRollbackDir(destinationRoot, manifest);
        Directory.CreateDirectory(rollbackDir);

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

            var restoreName = string.Equals(file.Conversion, ScanFileConversions.BmpToTiff, StringComparison.Ordinal)
                ? Path.GetFileName(file.DestinationPath)
                : file.FileName;
            var restorePath = Path.Combine(rollbackDir, restoreName);
            restorePath = ResolveRollbackPath(restorePath);
            File.Move(file.DestinationPath, restorePath, overwrite: false);
            restored++;
        }

        TryDeleteEmptyDirectory(finalDir);
        operationStore.WriteManifestAt(manifestPath, manifest with { RolledBackAt = DateTimeOffset.UtcNow });

        return new RollbackScanResult(true, manifestPath, rollbackDir, finalDir, restored, warnings);
    }

    private static string ResolveRollbackDir(string destinationRoot, OperationManifest manifest)
    {
        var rolledBackRoot = Path.Combine(destinationRoot, "_rolledback");
        var baseName = $"{ScanProcessor.SafePathToken(manifest.OrderNumber)}-{ScanProcessor.SafePathToken(manifest.RollNumber)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        var candidate = Path.Combine(rolledBackRoot, baseName);
        for (var suffix = 2; Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(rolledBackRoot, $"{baseName}-{suffix}");
        }

        return candidate;
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
