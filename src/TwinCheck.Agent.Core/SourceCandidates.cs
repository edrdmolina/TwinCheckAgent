namespace TwinCheck.Agent.Core;

public sealed record SourceCandidate(
    string Path,
    string Name,
    int ImageCount,
    DateTimeOffset ModifiedAt,
    bool IsConfiguredRoot);

public sealed class MultipleSourceCandidatesException(string sourceDir, string message) : InvalidOperationException(message)
{
    public string SourceDir { get; } = sourceDir;
}

public sealed class SourceCandidateService(AgentConfigProvider configProvider)
{
    public IReadOnlyList<SourceCandidate> GetCandidates(string? profileId, string? root)
    {
        var config = configProvider.Current;
        var profile = ResolveProfile(config, profileId);
        var configuredRoot = root ?? profile.SourceDir;
        var sourceRoot = FileSystemSafety.EnsureInsideAnyRoot(configuredRoot, config.AllowedSourceRoots, "source");

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceRoot}");
        }

        var candidates = new List<SourceCandidate>();
        var rootImageCount = CountImageFiles(sourceRoot);
        if (rootImageCount > 0)
        {
            candidates.Add(new SourceCandidate(
                sourceRoot,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot)),
                rootImageCount,
                GetCandidateModifiedAt(sourceRoot),
                true));
        }

        candidates.AddRange(Directory.EnumerateDirectories(sourceRoot)
            .Select(path => new SourceCandidate(
                path,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                CountImageFiles(path),
                GetCandidateModifiedAt(path),
                false))
            .Where(candidate => candidate.ImageCount > 0));

        return candidates
            .OrderByDescending(candidate => candidate.ModifiedAt)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static int CountImageFiles(string directory) =>
        Directory.EnumerateFiles(directory).Count(FileSystemSafety.IsImageFile);

    private static DateTimeOffset GetCandidateModifiedAt(string directory)
    {
        var newestImage = Directory.EnumerateFiles(directory)
            .Where(FileSystemSafety.IsImageFile)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(directory))
            .Max();

        return new DateTimeOffset(newestImage, TimeSpan.Zero);
    }
}
