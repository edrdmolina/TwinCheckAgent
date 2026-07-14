using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ReactiveUI;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Gui.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _agentName = "TwinCheck Scan Agent";
    private string _apiKey = "dev-local-key";
    private ProfileEditor? _selectedProfile;
    private string _statusMessage = "Ready.";

    public MainWindowViewModel()
    {
        Load();
    }

    public string AgentUrl => "https://localhost:3625";
    public string ConfigPath => LocalAgentConfigStore.ConfigPath;
    public ObservableCollection<ProfileEditor> Profiles { get; } = [];

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
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.Id = value;
        }
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
        get => SelectedProfile?.ScannerMode ?? ScannerModes.FrontierFolder;
        set
        {
            if (SelectedProfile is null) return;
            SelectedProfile.ScannerMode = value;
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

    public string SourceHealth
    {
        get
        {
            var source = ScannerMode == ScannerModes.NoritsuDailyWatch
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
        this.RaisePropertyChanged(nameof(SourceHealth));
        this.RaisePropertyChanged(nameof(DestinationHealth));
    }

    public void AddProfile()
    {
        var id = $"scanner-{Profiles.Count + 1}";
        var profile = new ProfileEditor
        {
            Id = id,
            Name = $"Scanner {Profiles.Count + 1}",
            ScannerMode = ScannerModes.FrontierFolder,
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
                    ScannerMode = ScannerModes.FrontierFolder,
                    NamingPattern = "{orderNumber}-{rollNumber}-{imgNumber}",
                }
            ]
        };
}

public sealed class ProfileEditor : ReactiveObject
{
    private string _id = "";
    private string _name = "";
    private string _scannerMode = ScannerModes.FrontierFolder;
    private string _sourceDir = "";
    private string _destinationDir = "";
    private string _namingPattern = "{orderNumber}-{rollNumber}-{imgNumber}";
    private bool _weeklyDestination = true;
    private int _settleStableSeconds = 5;
    private int _settleTimeoutSeconds = 120;
    private int _settlePollSeconds = 1;

    public string Id { get => _id; set => this.RaiseAndSetIfChanged(ref _id, value); }
    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string ScannerMode { get => _scannerMode; set => this.RaiseAndSetIfChanged(ref _scannerMode, value); }
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
            ScannerMode = string.IsNullOrWhiteSpace(profile.ScannerMode) ? ScannerModes.FrontierFolder : profile.ScannerMode,
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
            ScannerMode = ScannerModes.IsValid(ScannerMode) ? ScannerMode : ScannerModes.FrontierFolder,
            SourceDir = Path.GetFullPath(SourceDir),
            DestinationDir = Path.GetFullPath(DestinationDir),
            NamingPattern = string.IsNullOrWhiteSpace(NamingPattern) ? "{orderNumber}-{rollNumber}-{imgNumber}" : NamingPattern.Trim(),
            WeeklyDestination = WeeklyDestination,
            SettleStableSeconds = SettleStableSeconds,
            SettleTimeoutSeconds = SettleTimeoutSeconds,
            SettlePollSeconds = SettlePollSeconds,
        };

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";
}
