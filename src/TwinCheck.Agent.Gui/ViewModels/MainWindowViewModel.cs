using System;
using System.IO;
using System.Linq;
using ReactiveUI;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Gui.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _agentName = "TwinCheck Scan Agent";
    private string _apiKey = "dev-local-key";
    private string _profileId = "dev-sp500";
    private string _profileName = "Dev SP-500";
    private string _sourceDir = "/tmp/twincheck-agent/source/inbox";
    private string _destinationDir = "/tmp/twincheck-agent/destination";
    private string _namingPattern = "{orderNumber}-{rollNumber}-{imgNumber}";
    private string _statusMessage = "Ready.";

    public MainWindowViewModel()
    {
        Load();
    }

    public string AgentUrl => "https://localhost:3625";
    public string ConfigPath => LocalAgentConfigStore.ConfigPath;

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

    public string ProfileId
    {
        get => _profileId;
        set => this.RaiseAndSetIfChanged(ref _profileId, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set => this.RaiseAndSetIfChanged(ref _profileName, value);
    }

    public string SourceDir
    {
        get => _sourceDir;
        set
        {
            this.RaiseAndSetIfChanged(ref _sourceDir, value);
            this.RaisePropertyChanged(nameof(SourceHealth));
        }
    }

    public string DestinationDir
    {
        get => _destinationDir;
        set
        {
            this.RaiseAndSetIfChanged(ref _destinationDir, value);
            this.RaisePropertyChanged(nameof(DestinationHealth));
        }
    }

    public string NamingPattern
    {
        get => _namingPattern;
        set => this.RaiseAndSetIfChanged(ref _namingPattern, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string SourceHealth => Directory.Exists(SourceDir) ? "Source folder exists" : "Source folder is missing";

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
        var profile = config.Profiles.FirstOrDefault();

        AgentName = config.AgentName;
        ApiKey = config.ApiKey;
        ProfileId = profile?.Id ?? config.ActiveProfileId ?? "dev-sp500";
        ProfileName = profile?.Name ?? "Dev SP-500";
        SourceDir = profile?.SourceDir ?? "/tmp/twincheck-agent/source/inbox";
        DestinationDir = profile?.DestinationDir ?? "/tmp/twincheck-agent/destination";
        NamingPattern = profile?.NamingPattern ?? "{orderNumber}-{rollNumber}-{imgNumber}";
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
            AllowedSourceRoots = [Path.GetFullPath(SourceDir)],
            AllowedDestinationRoots = [Path.GetFullPath(DestinationDir)],
            ActiveProfileId = string.IsNullOrWhiteSpace(ProfileId) ? "dev-sp500" : ProfileId.Trim(),
            Profiles =
            [
                new ScannerProfile
                {
                    Id = string.IsNullOrWhiteSpace(ProfileId) ? "dev-sp500" : ProfileId.Trim(),
                    Name = string.IsNullOrWhiteSpace(ProfileName) ? "Dev SP-500" : ProfileName.Trim(),
                    SourceDir = Path.GetFullPath(SourceDir),
                    DestinationDir = Path.GetFullPath(DestinationDir),
                    NamingPattern = string.IsNullOrWhiteSpace(NamingPattern)
                        ? "{orderNumber}-{rollNumber}-{imgNumber}"
                        : NamingPattern.Trim(),
                }
            ]
        };

        LocalAgentConfigStore.Save(config);
        StatusMessage = $"Saved config. The API will use these settings on the next request. {DateTime.Now:t}";
        this.RaisePropertyChanged(nameof(SourceHealth));
        this.RaisePropertyChanged(nameof(DestinationHealth));
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
                    Name = "Dev SP-500",
                    SourceDir = "/tmp/twincheck-agent/source/inbox",
                    DestinationDir = "/tmp/twincheck-agent/destination",
                    NamingPattern = "{orderNumber}-{rollNumber}-{imgNumber}",
                }
            ]
        };
}
