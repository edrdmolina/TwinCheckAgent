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
    OperationSummary? LastOperation,
    IReadOnlyList<string> Warnings);

public sealed record ScannerProfileHealth(
    string Id,
    string Name,
    bool SourceExists,
    bool DestinationExists,
    bool DestinationWritable);

public sealed record OperationSummary(
    string IdempotencyKey,
    DateTimeOffset CompletedAt,
    string OrderNumber,
    string RollNumber,
    int ImageCount,
    bool Ok);

public sealed class HealthService(AgentConfigProvider configProvider, OperationStore operationStore)
{
    public HealthService(AgentConfig config, OperationStore operationStore)
        : this(new AgentConfigProvider(config), operationStore)
    {
    }

    public AgentHealth GetHealth(string agentUrl)
    {
        var config = configProvider.Current;
        var profileHealth = config.Profiles
            .Select(profile => new ScannerProfileHealth(
                profile.Id,
                profile.Name,
                Directory.Exists(profile.SourceDir),
                Directory.Exists(profile.DestinationDir),
                Directory.Exists(profile.DestinationDir) && FileSystemSafety.CanWriteToDirectory(profile.DestinationDir)))
            .ToArray();

        var writableRoots = config.AllowedDestinationRoots
            .Where(root => Directory.Exists(root) && FileSystemSafety.CanWriteToDirectory(root))
            .ToArray();

        var warnings = new List<string>();
        if (config.ApiKey == "change-me")
        {
            warnings.Add("Default API key is still configured.");
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
            LastOperation: operationStore.GetLastOperation(),
            Warnings: warnings);
    }
}
