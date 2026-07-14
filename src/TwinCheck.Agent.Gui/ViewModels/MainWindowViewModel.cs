using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ReactiveUI;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Gui.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _agentName = "TwinCheck Scan Agent";
    private string _apiKey = "dev-local-key";
    private ProfileEditor? _selectedProfile;
    private string _statusMessage = "Ready.";
    private string _selectedPage = "Overview";
    private string _apiStatus = "Not checked";
    private string _apiStatusDetail = "Click Refresh to check the local API.";
    private string _apiStatusColor = "#64748B";
    private string _readinessSummary = "Unknown";
    private string _lastOperationSummary = "No operation loaded.";
    private string _activeWatchesSummary = "No active watches.";
    private string _diagnosticsText = "Diagnostics have not been loaded.";
    private string _recentLogsText = "Logs have not been loaded.";

    public MainWindowViewModel()
    {
        Load();
    }

    public string AgentUrl => "https://localhost:3625";
    public string ConfigPath => LocalAgentConfigStore.ConfigPath;
    public string LogDirectory => LocalAgentLogger.DefaultLogDirectory;
    public string SetupServiceName => OperatingSystem.IsWindows() ? "TwinCheck Scan Agent" : "twincheck-scan-agent";
    public ObservableCollection<ProfileEditor> Profiles { get; } = [];
    public ObservableCollection<ScannerModeOption> ScannerModeOptions { get; } =
    [
        new("Frontier Polling Watcher", ScannerModes.FrontierPollingWatch),
        new("Frontier Sentinel Watcher", ScannerModes.FrontierSentinelWatch),
        new("Noritsu Watcher", ScannerModes.NoritsuWatch),
    ];

    public string AgentName
    {
        get => _agentName;
        set => this.RaiseAndSetIfChanged(ref _agentName, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => this.RaiseAndSetIfChanged(ref _apiKey, value);
    }

    public ProfileEditor? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedProfile, value);
            this.RaisePropertyChanged(nameof(ProfileId));
            this.RaisePropertyChanged(nameof(ProfileName));
            this.RaisePropertyChanged(nameof(ScannerMode));
            this.RaisePropertyChanged(nameof(SelectedScannerModeOption));
            this.RaisePropertyChanged(nameof(SourceDir));
            this.RaisePropertyChanged(nameof(DestinationDir));
            this.RaisePropertyChanged(nameof(NamingPattern));
            this.RaisePropertyChanged(nameof(WeeklyDestination));
            this.RaisePropertyChanged(nameof(SettleStableSeconds));
            this.RaisePropertyChanged(nameof(SettleTimeoutSeconds));
            this.RaisePropertyChanged(nameof(SettlePollSeconds));
            this.RaisePropertyChanged(nameof(SourceHealth));
            this.RaisePropertyChanged(nameof(DestinationHealth));
        }
    }

    public string ProfileId
    {
        get => SelectedProfile?.Id ?? "";
    }

    public string ProfileName
    {
        get => SelectedProfile?.Name ?? "";
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.Name = value;
        }
    }

    public string ScannerMode
    {
        get => SelectedProfile?.ScannerMode ?? ScannerModes.FrontierPollingWatch;
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.ScannerMode = ScannerModes.NormalizeOrDefault(value);
            this.RaisePropertyChanged(nameof(SelectedScannerModeOption));
            this.RaisePropertyChanged(nameof(SourceHealth));
        }
    }

    public ScannerModeOption? SelectedScannerModeOption
    {
        get => ScannerModeOptions.FirstOrDefault(option => option.Value == ScannerModes.NormalizeOrDefault(ScannerMode));
        set
        {
            if (value is null) return;
            ScannerMode = value.Value;
        }
    }

    public string SourceDir
    {
        get => SelectedProfile?.SourceDir ?? "";
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.SourceDir = value;
            this.RaisePropertyChanged(nameof(SourceHealth));
        }
    }

    public string DestinationDir
    {
        get => SelectedProfile?.DestinationDir ?? "";
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.DestinationDir = value;
            this.RaisePropertyChanged(nameof(DestinationHealth));
        }
    }

    public string NamingPattern
    {
        get => SelectedProfile?.NamingPattern ?? "{orderNumber}-{rollNumber}-{imgNumber}";
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.NamingPattern = value;
        }
    }

    public bool WeeklyDestination
    {
        get => SelectedProfile?.WeeklyDestination ?? true;
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.WeeklyDestination = value;
        }
    }

    public int SettleStableSeconds
    {
        get => SelectedProfile?.SettleStableSeconds ?? 5;
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.SettleStableSeconds = value;
        }
    }

    public int SettleTimeoutSeconds
    {
        get => SelectedProfile?.SettleTimeoutSeconds ?? 120;
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.SettleTimeoutSeconds = value;
        }
    }

    public int SettlePollSeconds
    {
        get => SelectedProfile?.SettlePollSeconds ?? 1;
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.SettlePollSeconds = value;
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string SelectedPage
    {
        get => _selectedPage;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPage, value);
            this.RaisePropertyChanged(nameof(IsOverviewVisible));
            this.RaisePropertyChanged(nameof(IsProfilesVisible));
            this.RaisePropertyChanged(nameof(IsDiagnosticsVisible));
            this.RaisePropertyChanged(nameof(IsLogsVisible));
            this.RaisePropertyChanged(nameof(IsSetupVisible));
            this.RaisePropertyChanged(nameof(PageTitle));
        }
    }

    public string PageTitle => SelectedPage;
    public bool IsOverviewVisible => SelectedPage == "Overview";
    public bool IsProfilesVisible => SelectedPage == "Profiles";
    public bool IsDiagnosticsVisible => SelectedPage == "Diagnostics";
    public bool IsLogsVisible => SelectedPage == "Logs";
    public bool IsSetupVisible => SelectedPage == "Setup";

    public string ApiStatus
    {
        get => _apiStatus;
        set => this.RaiseAndSetIfChanged(ref _apiStatus, value);
    }

    public string ApiStatusDetail
    {
        get => _apiStatusDetail;
        set => this.RaiseAndSetIfChanged(ref _apiStatusDetail, value);
    }

    public string ApiStatusColor
    {
        get => _apiStatusColor;
        set => this.RaiseAndSetIfChanged(ref _apiStatusColor, value);
    }

    public string ReadinessSummary
    {
        get => _readinessSummary;
        set => this.RaiseAndSetIfChanged(ref _readinessSummary, value);
    }

    public string LastOperationSummary
    {
        get => _lastOperationSummary;
        set => this.RaiseAndSetIfChanged(ref _lastOperationSummary, value);
    }

    public string ActiveWatchesSummary
    {
        get => _activeWatchesSummary;
        set => this.RaiseAndSetIfChanged(ref _activeWatchesSummary, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        set => this.RaiseAndSetIfChanged(ref _diagnosticsText, value);
    }

    public string RecentLogsText
    {
        get => _recentLogsText;
        set => this.RaiseAndSetIfChanged(ref _recentLogsText, value);
    }

    public string ApiKeyWarning =>
        ApiKey is "change-me" or "dev-local-key"
            ? "Default API key is still configured. Generate a unique key before production use."
            : "API key is unique.";

    public string SourceHealth
    {
        get
        {
            var source = ScannerModes.NormalizeOrDefault(ScannerMode) == ScannerModes.NoritsuWatch
                ? ScannerFileSystem.GetNoritsuDailyFolder(SourceDir)
                : SourceDir;
            return Directory.Exists(source) ? $"Source folder exists: {source}" : $"Source folder is missing: {source}";
        }
    }

    public string DestinationHealth
    {
        get
        {
            if (!Directory.Exists(DestinationDir))
            {
                return "Destination folder is missing";
            }

            return FileSystemSafety.CanWriteToDirectory(DestinationDir)
                ? "Destination is writable"
                : "Destination is not writable";
        }
    }

    public void Load()
    {
        var config = LocalAgentConfigStore.LoadOrDefault(CreateDefaultConfig());

        AgentName = config.AgentName;
        ApiKey = config.ApiKey;
        Profiles.Clear();
        foreach (var profile in config.Profiles.Length > 0 ? config.Profiles : CreateDefaultConfig().Profiles)
        {
            Profiles.Add(ProfileEditor.FromProfile(profile));
        }

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == config.ActiveProfileId) ?? Profiles.FirstOrDefault();
        StatusMessage = File.Exists(LocalAgentConfigStore.ConfigPath)
            ? "Loaded local agent config."
            : "Using default config. Save to create the local config file.";
        RefreshAllComputed();
    }

    public void Save()
    {
        var config = new AgentConfig
        {
            AgentName = string.IsNullOrWhiteSpace(AgentName) ? "TwinCheck Scan Agent" : AgentName.Trim(),
            Version = "0.1.0",
            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? "dev-local-key" : ApiKey.Trim(),
            AllowedSourceRoots = Profiles.Select(profile => Path.GetFullPath(profile.SourceDir)).Distinct().ToArray(),
            AllowedDestinationRoots = Profiles.Select(profile => Path.GetFullPath(profile.DestinationDir)).Distinct().ToArray(),
            ActiveProfileId = string.IsNullOrWhiteSpace(SelectedProfile?.Id) ? Profiles.FirstOrDefault()?.Id : SelectedProfile.Id.Trim(),
            Profiles = Profiles.Select(profile => profile.ToProfile()).ToArray()
        };

        LocalAgentConfigStore.Save(config);
        StatusMessage = $"Saved config. The API will use these settings on the next request. {DateTime.Now:t}";
        RefreshAllComputed();
    }

    public void AddProfile()
    {
        var id = NextProfileId();
        var profile = new ProfileEditor
        {
            Id = id,
            Name = $"Scanner {Profiles.Count + 1}",
            ScannerMode = ScannerModes.FrontierPollingWatch,
            SourceDir = SourceDir,
            DestinationDir = DestinationDir,
            NamingPattern = "{orderNumber}-{rollNumber}-{imgNumber}",
            WeeklyDestination = true,
            SettleStableSeconds = 5,
            SettleTimeoutSeconds = 120,
            SettlePollSeconds = 1,
        };
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    private string NextProfileId()
    {
        var next = Profiles.Count + 1;
        while (Profiles.Any(profile => string.Equals(profile.Id, $"scanner-{next}", StringComparison.OrdinalIgnoreCase)))
        {
            next++;
        }

        return $"scanner-{next}";
    }

    public void DeleteSelectedProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1)
        {
            StatusMessage = "At least one profile is required.";
            return;
        }

        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Max(0, Math.Min(index, Profiles.Count - 1))];
    }

    public void GenerateApiKey()
    {
        ApiKey = $"tcn-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        StatusMessage = "Generated a new API key. Save profile and update TwinCheckN browser settings.";
        this.RaisePropertyChanged(nameof(ApiKeyWarning));
    }

    public void Navigate(string page)
    {
        if (!string.IsNullOrWhiteSpace(page))
        {
            SelectedPage = page;
        }
    }

    public async Task RefreshAsync()
    {
        StatusMessage = "Checking local agent...";
        await RefreshApiStatus();
        RefreshLogs();
        StatusMessage = $"Refreshed {DateTime.Now:t}";
    }

    public string BuildDiagnosticsClipboardText() =>
        string.Join(Environment.NewLine, new[]
        {
            $"TwinCheck Scan Agent diagnostics ({DateTimeOffset.Now})",
            $"API status: {ApiStatus}",
            $"API detail: {ApiStatusDetail}",
            $"Agent URL: {AgentUrl}",
            $"Config path: {ConfigPath}",
            $"Log directory: {LogDirectory}",
            $"Selected profile: {SelectedProfile?.Name ?? "None"} ({SelectedProfile?.Id ?? "none"})",
            $"Source: {SourceDir}",
            $"Destination: {DestinationDir}",
            $"Readiness: {ReadinessSummary}",
            "",
            DiagnosticsText
        });

    private void RefreshAllComputed()
    {
        this.RaisePropertyChanged(nameof(SourceHealth));
        this.RaisePropertyChanged(nameof(DestinationHealth));
        this.RaisePropertyChanged(nameof(ApiKeyWarning));
        this.RaisePropertyChanged(nameof(LogDirectory));
        this.RaisePropertyChanged(nameof(SetupServiceName));
    }

    private async Task RefreshApiStatus()
    {
        try
        {
            using var document = await GetAgentJson("/api/scan/diagnostics");
            var diagnostics = document.RootElement.GetProperty("diagnostics");
            var warnings = diagnostics.TryGetProperty("warnings", out var warningElement) && warningElement.ValueKind == JsonValueKind.Array
                ? warningElement.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : [];
            var profiles = diagnostics.TryGetProperty("profiles", out var profileElement) && profileElement.ValueKind == JsonValueKind.Array
                ? profileElement.EnumerateArray().ToArray()
                : [];
            var activeWatches = diagnostics.TryGetProperty("activeWatches", out var watchElement) && watchElement.ValueKind == JsonValueKind.Array
                ? watchElement.GetArrayLength()
                : 0;

            ApiStatus = warnings.Length == 0 ? "Connected" : "Connected with warnings";
            ApiStatusColor = warnings.Length == 0 ? "#16A34A" : "#D97706";
            ApiStatusDetail = $"{profiles.Length} profile(s), {warnings.Length} warning(s).";
            ReadinessSummary = warnings.Length == 0 ? "Ready" : string.Join("  ", warnings);
            ActiveWatchesSummary = activeWatches == 0 ? "No active watches." : $"{activeWatches} active watch(es).";
            LastOperationSummary = FormatLastOperation(diagnostics);
            DiagnosticsText = FormatDiagnostics(diagnostics);
        }
        catch (Exception exception)
        {
            ApiStatus = "API unreachable";
            ApiStatusColor = "#DC2626";
            ApiStatusDetail = exception.Message;
            ReadinessSummary = $"Start or restart the {SetupServiceName} service, then refresh.";
            DiagnosticsText = $"API check failed: {exception.Message}";
        }
    }

    private void RefreshLogs()
    {
        try
        {
            RecentLogsText = string.Join(Environment.NewLine, new LocalAgentLogger().ReadRecentLines(250));
            if (string.IsNullOrWhiteSpace(RecentLogsText))
            {
                RecentLogsText = "No local log entries yet.";
            }
        }
        catch (Exception exception)
        {
            RecentLogsText = $"Could not read logs: {exception.Message}";
        }
    }

    private async Task<JsonDocument> GetAgentJson(string path)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) =>
                message.RequestUri?.Host is "localhost" or "127.0.0.1"
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, AgentUrl + path);
        request.Headers.Add("X-Api-Key", ApiKey);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string FormatLastOperation(JsonElement diagnostics)
    {
        if (!diagnostics.TryGetProperty("lastOperation", out var operation) || operation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "No recent operation.";
        }

        var order = operation.TryGetProperty("orderNumber", out var orderValue) ? orderValue.GetString() : "";
        var roll = operation.TryGetProperty("rollNumber", out var rollValue) ? rollValue.GetString() : "";
        var count = operation.TryGetProperty("imageCount", out var countValue) ? countValue.GetInt32() : 0;
        var completed = operation.TryGetProperty("completedAt", out var completedValue) ? completedValue.GetString() : "";
        return $"{order}-{roll}: {count} image(s), completed {completed}";
    }

    private static string FormatDiagnostics(JsonElement diagnostics)
    {
        var lines = new Collection<string>
        {
            $"Agent: {diagnostics.GetProperty("agentName").GetString()} {diagnostics.GetProperty("version").GetString()}",
            $"Host: {diagnostics.GetProperty("hostname").GetString()}",
            $"OS: {diagnostics.GetProperty("operatingSystem").GetString()} ({diagnostics.GetProperty("processArchitecture").GetString()})",
            $"Config: {diagnostics.GetProperty("configPath").GetString()}",
            $"Logs: {diagnostics.GetProperty("logDirectory").GetString()}",
            ""
        };

        if (diagnostics.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            lines.Add("Profiles");
            foreach (var profile in profiles.EnumerateArray())
            {
                lines.Add($"- {profile.GetProperty("name").GetString()} ({profile.GetProperty("scannerMode").GetString()}): {profile.GetProperty("readiness").GetString()}");
                lines.Add($"  Source: {profile.GetProperty("sourcePath").GetString()}");
                lines.Add($"  Destination: {profile.GetProperty("destinationPath").GetString()}");
                lines.Add($"  Candidates: {profile.GetProperty("candidateCount").GetInt32()}");
            }
        }

        if (diagnostics.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0)
        {
            lines.Add("");
            lines.Add("Warnings");
            foreach (var warning in warnings.EnumerateArray())
            {
                lines.Add($"- {warning.GetString()}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static AgentConfig CreateDefaultConfig() =>
        new()
        {
            AgentName = "TwinCheck Scan Agent",
            Version = "0.1.0",
            ApiKey = "dev-local-key",
            AllowedSourceRoots = ["/tmp/twincheck-agent/source/inbox"],
            AllowedDestinationRoots = ["/tmp/twincheck-agent/destination"],
            ActiveProfileId = "dev-sp500",
            Profiles =
            [
                new ScannerProfile
                {
                    Id = "dev-sp500",
                    Name = "Dev SP-500 Frontier",
                    SourceDir = "/tmp/twincheck-agent/source/inbox",
                    DestinationDir = "/tmp/twincheck-agent/destination",
                    ScannerMode = ScannerModes.FrontierPollingWatch,
                    NamingPattern = "{orderNumber}-{rollNumber}-{imgNumber}",
                }
            ]
        };
}

public sealed class ProfileEditor : ReactiveObject
{
    private string _id = "";
    private string _name = "";
    private string _scannerMode = ScannerModes.FrontierPollingWatch;
    private string _sourceDir = "";
    private string _destinationDir = "";
    private string _namingPattern = "{orderNumber}-{rollNumber}-{imgNumber}";
    private bool _weeklyDestination = true;
    private int _settleStableSeconds = 5;
    private int _settleTimeoutSeconds = 120;
    private int _settlePollSeconds = 1;

    public string Id { get => _id; set => this.RaiseAndSetIfChanged(ref _id, value); }
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string ScannerMode { get => _scannerMode; set => this.RaiseAndSetIfChanged(ref _scannerMode, ScannerModes.NormalizeOrDefault(value)); }
    public string SourceDir { get => _sourceDir; set => this.RaiseAndSetIfChanged(ref _sourceDir, value); }
    public string DestinationDir { get => _destinationDir; set => this.RaiseAndSetIfChanged(ref _destinationDir, value); }
    public string NamingPattern { get => _namingPattern; set => this.RaiseAndSetIfChanged(ref _namingPattern, value); }
    public bool WeeklyDestination { get => _weeklyDestination; set => this.RaiseAndSetIfChanged(ref _weeklyDestination, value); }
    public int SettleStableSeconds { get => _settleStableSeconds; set => this.RaiseAndSetIfChanged(ref _settleStableSeconds, Math.Max(0, value)); }
    public int SettleTimeoutSeconds { get => _settleTimeoutSeconds; set => this.RaiseAndSetIfChanged(ref _settleTimeoutSeconds, Math.Max(1, value)); }
    public int SettlePollSeconds { get => _settlePollSeconds; set => this.RaiseAndSetIfChanged(ref _settlePollSeconds, Math.Max(1, value)); }

    public static ProfileEditor FromProfile(ScannerProfile profile) =>
        new()
        {
            Id = profile.Id,
            Name = profile.Name,
            ScannerMode = ScannerModes.NormalizeOrDefault(profile.ScannerMode),
            SourceDir = profile.SourceDir,
            DestinationDir = profile.DestinationDir,
            NamingPattern = profile.NamingPattern,
            WeeklyDestination = profile.WeeklyDestination,
            SettleStableSeconds = profile.SettleStableSeconds,
            SettleTimeoutSeconds = profile.SettleTimeoutSeconds,
            SettlePollSeconds = profile.SettlePollSeconds,
        };

    public ScannerProfile ToProfile() =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? $"scanner-{Guid.NewGuid():N}" : Id.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? Id.Trim() : Name.Trim(),
            ScannerMode = ScannerModes.NormalizeOrDefault(ScannerMode),
            SourceDir = Path.GetFullPath(SourceDir),
            DestinationDir = Path.GetFullPath(DestinationDir),
            NamingPattern = string.IsNullOrWhiteSpace(NamingPattern) ? "{orderNumber}-{rollNumber}-{imgNumber}" : NamingPattern.Trim(),
            WeeklyDestination = WeeklyDestination,
            SettleStableSeconds = SettleStableSeconds,
            SettleTimeoutSeconds = SettleTimeoutSeconds,
            SettlePollSeconds = SettlePollSeconds,
        };

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed record ScannerModeOption(string Label, string Value)
{
    public override string ToString() => Label;
}
