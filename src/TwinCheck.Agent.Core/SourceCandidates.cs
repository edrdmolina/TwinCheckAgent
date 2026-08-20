namespace TwinCheck.Agent.Core;

public sealed record SourceCandidate(
    string Path,
    string Name,
    int ImageCount,
    DateTimeOffset ModifiedAt,
    bool IsConfiguredRoot,
    string ScannerMode,
    string? DestinationPreview,
    bool HasSentinel = false,
    bool ReadyForSelection = true,
    string? ReadinessMessage = null);

public sealed class MultipleSourceCandidatesException(string sourceDir, string message) : InvalidOperationException(message)
{
    public string SourceDir { get; } = sourceDir;
}

public sealed class SourceCandidateService(AgentConfigProvider configProvider)
{
    public IReadOnlyList<SourceCandidate> GetCandidates(
        string? profileId,
        string? root,
        string? orderNumber = null,
        string? rollNumber = null,
        string? scanKind = null,
        int? rescanNumber = null)
    {
        var config = configProvider.Current;
        var profile = ResolveProfile(config, profileId);
        var configuredRoot = root ?? ResolveCandidateRoot(profile);
        var sourceRoot = FileSystemSafety.EnsureInsideAnyRoot(configuredRoot, config.AllowedSourceRoots, "source");
        if (ScannerModes.Normalize(profile.ScannerMode) == ScannerModes.NoritsuWatch
            && string.IsNullOrWhiteSpace(root))
        {
            Directory.CreateDirectory(sourceRoot);
        }

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceRoot}");
        }

        var candidates = new List<SourceCandidate>();
        var rootImageCount = ScannerFileSystem.CountImageFiles(sourceRoot);
        if (rootImageCount > 0)
        {
            candidates.Add(CreateCandidate(
                profile,
                sourceRoot,
                rootImageCount,
                true,
                orderNumber,
                rollNumber,
                scanKind,
                rescanNumber));
        }

        candidates.AddRange(Directory.EnumerateDirectories(sourceRoot)
            .Select(path => CreateCandidate(
                profile,
                path,
                ScannerFileSystem.CountImageFiles(path),
                false,
                orderNumber,
                rollNumber,
                scanKind,
                rescanNumber))
            .Where(candidate => candidate.ImageCount > 0));

        return candidates
            .OrderByDescending(candidate => candidate.ModifiedAt)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ResolveCandidateRoot(ScannerProfile profile) =>
        ScannerModes.Normalize(profile.ScannerMode) == ScannerModes.NoritsuWatch
            ? ScannerFileSystem.GetNoritsuDailyFolder(profile.SourceDir)
            : profile.SourceDir;

    private static SourceCandidate CreateCandidate(
        ScannerProfile profile,
        string path,
        int imageCount,
        bool isConfiguredRoot,
        string? orderNumber,
        string? rollNumber,
        string? scanKind,
        int? rescanNumber)
    {
        var scannerMode = ScannerModes.NormalizeOrDefault(profile.ScannerMode);
        var hasSentinel = File.Exists(Path.Combine(path, FileSystemSafety.ExportSentinelFileName));
        var readyForSelection = scannerMode != ScannerModes.FrontierSentinelWatch || hasSentinel;
        var destinationPreview = !string.IsNullOrWhiteSpace(orderNumber) && !string.IsNullOrWhiteSpace(rollNumber)
            ? ScanProcessor.BuildFinalDirectoryPreview(
                profile.DestinationDir,
                orderNumber,
                rollNumber,
                profile.WeeklyDestination,
                scanKind ?? ScanKinds.Original,
                rescanNumber)
            : null;

        return new SourceCandidate(
            path,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            imageCount,
            ScannerFileSystem.GetNewestImageModifiedAt(path),
            isConfiguredRoot,
            scannerMode,
            destinationPreview,
            hasSentinel,
            readyForSelection,
            readyForSelection
                ? scannerMode == ScannerModes.FrontierSentinelWatch
                    ? $"{FileSystemSafety.ExportSentinelFileName} found; stability will be verified before processing."
                    : "Folder will be checked for stability before processing."
                : $"Waiting for {FileSystemSafety.ExportSentinelFileName}.");
    }

    private static ScannerProfile ResolveProfile(AgentConfig config, string? profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            return config.Profiles.SingleOrDefault(profile => profile.Id == profileId)
                ?? throw new InvalidOperationException($"Unknown scanner profile '{profileId}'.");
        }

        if (!string.IsNullOrWhiteSpace(config.ActiveProfileId))
        {
            var active = config.Profiles.SingleOrDefault(profile => profile.Id == config.ActiveProfileId);
            if (active is not null)
            {
                return active;
            }
        }

        return config.Profiles.FirstOrDefault()
            ?? throw new InvalidOperationException("No scanner profiles are configured.");
    }

}
