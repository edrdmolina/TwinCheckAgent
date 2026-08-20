using System.Runtime.InteropServices;

namespace TwinCheck.Agent.Core;

public sealed record AgentDiagnostics(
    string AgentName,
    string Version,
    string Hostname,
    string AgentUrl,
    IReadOnlyList<string> Capabilities,
    string OperatingSystem,
    string ProcessArchitecture,
    string ConfigPath,
    string LogDirectory,
    bool DefaultApiKey,
    IReadOnlyList<ScannerProfileDiagnostic> Profiles,
    IReadOnlyList<ScanWatchState> ActiveWatches,
    IReadOnlyList<ScanOperationView> ActiveOperations,
    OperationSummary? LastOperation,
    IReadOnlyList<string> Warnings);

public sealed record ScannerProfileDiagnostic(
    string Id,
    string Name,
    string ScannerMode,
    string SourcePath,
    bool SourceExists,
    string DestinationPath,
    bool DestinationExists,
    bool DestinationWritable,
    int CandidateCount,
    string Readiness);

public sealed class DiagnosticsService(
    AgentConfigProvider configProvider,
    OperationStore operationStore,
    ScanWatchService? watchService = null,
    ScanOperationService? operationService = null)
{
    public AgentDiagnostics GetDiagnostics(string agentUrl)
    {
        var config = configProvider.Current;
        var profiles = config.Profiles.Select(ToProfileDiagnostic).ToArray();
        var warnings = new List<string>();

        if (config.ApiKey is "change-me" or "dev-local-key")
        {
            warnings.Add("Default API key is still configured.");
        }

        warnings.AddRange(profiles
            .Where(profile => profile.Readiness != "Ready")
            .Select(profile => $"{profile.Name}: {profile.Readiness}"));

        warnings.AddRange(config.Profiles
            .Where(profile => ScannerModes.NormalizeOrDefault(profile.ScannerMode) == ScannerModes.FrontierPollingWatch
                && (profile.SettleStableSeconds < 30 || profile.SettleTimeoutSeconds < 600))
            .Select(profile => $"{profile.Name}: polling settle window is short; use at least 30 stable seconds and a 600-second timeout, or prefer export.done sentinel mode."));

        return new AgentDiagnostics(
            AgentName: config.AgentName,
            Version: config.Version,
            Hostname: Environment.MachineName,
            AgentUrl: agentUrl,
            Capabilities: ["async-operations-v1"],
            OperatingSystem: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            ConfigPath: LocalAgentConfigStore.ConfigPath,
            LogDirectory: LocalAgentLogger.DefaultLogDirectory,
            DefaultApiKey: config.ApiKey is "change-me" or "dev-local-key",
            Profiles: profiles,
            ActiveWatches: watchService?.ListActive() ?? [],
            ActiveOperations: operationService?.ListActive() ?? [],
            LastOperation: operationStore.GetLastOperation(),
            Warnings: warnings);
    }

    private static ScannerProfileDiagnostic ToProfileDiagnostic(ScannerProfile profile)
    {
        var sourcePath = SourceCandidateService.ResolveCandidateRoot(profile);
        var sourceExists = Directory.Exists(sourcePath);
        var destinationExists = Directory.Exists(profile.DestinationDir);
        var destinationWritable = destinationExists && FileSystemSafety.CanWriteToDirectory(profile.DestinationDir);
        var candidateCount = CountCandidates(sourcePath);
        var readiness = sourceExists && destinationWritable
            ? "Ready"
            : !sourceExists
                ? "Source folder is missing"
                : "Destination is missing or not writable";

        return new ScannerProfileDiagnostic(
            profile.Id,
            profile.Name,
            ScannerModes.NormalizeOrDefault(profile.ScannerMode),
            sourcePath,
            sourceExists,
            profile.DestinationDir,
            destinationExists,
            destinationWritable,
            candidateCount,
            readiness);
    }

    private static int CountCandidates(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var count = ScannerFileSystem.CountImageFiles(root) > 0 ? 1 : 0;
        count += Directory.EnumerateDirectories(root).Count(path => ScannerFileSystem.CountImageFiles(path) > 0);
        return count;
    }
}
