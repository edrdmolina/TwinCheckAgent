namespace TwinCheck.Agent.Core;

public sealed record AgentHealth(
    bool Ok,
    string AgentName,
    string Version,
    string Hostname,
    string AgentUrl,
    IReadOnlyList<ScannerProfileHealth> Profiles,
    string? ActiveProfileId,
    bool NasMounted,
    IReadOnlyList<string> WritableRoots,
    IReadOnlyList<ScanWatchState> ActiveWatches,
    OperationSummary? LastOperation,
    IReadOnlyList<string> Warnings);

public sealed record ScannerProfileHealth(
    string Id,
    string Name,
    string ScannerMode,
    bool SourceExists,
    bool DestinationExists,
    bool DestinationWritable,
    int CandidateCount);

public sealed record OperationSummary(
    string IdempotencyKey,
    DateTimeOffset CompletedAt,
    string OrderNumber,
    string RollNumber,
    int ImageCount,
    bool Ok);

public sealed class HealthService(AgentConfigProvider configProvider, OperationStore operationStore, ScanWatchService? watchService = null)
{
    public HealthService(AgentConfig config, OperationStore operationStore)
        : this(new AgentConfigProvider(config), operationStore, null)
    {
    }

    public AgentHealth GetHealth(string agentUrl)
    {
        var config = configProvider.Current;
        var profileHealth = config.Profiles
            .Select(profile => new ScannerProfileHealth(
                profile.Id,
                profile.Name,
                profile.ScannerMode,
                Directory.Exists(SourceCandidateService.ResolveCandidateRoot(profile)),
                Directory.Exists(profile.DestinationDir),
                Directory.Exists(profile.DestinationDir) && FileSystemSafety.CanWriteToDirectory(profile.DestinationDir),
                CountCandidates(profile)))
            .ToArray();

        var writableRoots = config.AllowedDestinationRoots
            .Where(root => Directory.Exists(root) && FileSystemSafety.CanWriteToDirectory(root))
            .ToArray();

        var warnings = new List<string>();
        if (config.ApiKey is "change-me" or "dev-local-key")
        {
            warnings.Add("Default API key is still configured. Generate a unique machine key before production use.");
        }

        foreach (var profile in profileHealth.Where(profile => !profile.SourceExists || !profile.DestinationWritable))
        {
            warnings.Add($"Profile '{profile.Name}' has unavailable source or destination paths.");
        }

        return new AgentHealth(
            Ok: profileHealth.Length > 0 && profileHealth.Any(profile => profile.SourceExists && profile.DestinationWritable),
            AgentName: config.AgentName,
            Version: config.Version,
            Hostname: Environment.MachineName,
            AgentUrl: agentUrl,
            Profiles: profileHealth,
            ActiveProfileId: config.ActiveProfileId,
            NasMounted: writableRoots.Length > 0,
            WritableRoots: writableRoots,
            ActiveWatches: watchService?.ListActive() ?? [],
            LastOperation: operationStore.GetLastOperation(),
            Warnings: warnings);
    }

    private static int CountCandidates(ScannerProfile profile)
    {
        var root = SourceCandidateService.ResolveCandidateRoot(profile);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var count = ScannerFileSystem.CountImageFiles(root) > 0 ? 1 : 0;
        count += Directory.EnumerateDirectories(root).Count(path => ScannerFileSystem.CountImageFiles(path) > 0);
        return count;
    }
}
