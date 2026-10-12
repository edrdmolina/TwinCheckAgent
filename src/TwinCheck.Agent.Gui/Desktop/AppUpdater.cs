using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using TwinCheck.Agent.Core;
using Velopack;
using Velopack.Sources;

namespace TwinCheck.Agent.Gui.Desktop;

/// <summary>
/// Checks GitHub Releases for a newer build and downloads it in the background. A downloaded
/// update is applied when the operator clicks Restart to update, or automatically the next time
/// the app starts (Velopack applies pending updates on startup).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppUpdater : ReactiveObject
{
    private const string RepositoryUrl = "https://github.com/edrdmolina/TwinCheckAgent";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(5);

    private readonly UpdateManager manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private readonly DesktopAgent agent;
    private readonly LocalAgentLogger logger = new();
    private readonly DispatcherTimer checkTimer;
    private readonly DispatcherTimer jobTimer;
    private VelopackAsset? ready;
    private int activeJobs;
    private bool isChecking;
    private bool isApplying;

    public AppUpdater(DesktopAgent agent)
    {
        this.agent = agent;
        checkTimer = new DispatcherTimer { Interval = CheckInterval };
        checkTimer.Tick += async (_, _) => await CheckAsync();
        jobTimer = new DispatcherTimer { Interval = JobPollInterval };
        jobTimer.Tick += (_, _) => RefreshActiveJobs();
    }

    public bool BannerVisible => ready is not null;

    public string BannerText => ready is null
        ? ""
        : activeJobs > 0
            ? $"Update {ready.Version} is ready. Waiting for {activeJobs} running job(s) to finish before it can be installed."
            : $"Update {ready.Version} is ready. Restarting takes a few seconds; it also installs automatically the next time the app starts.";

    public bool CanApply => ready is not null && activeJobs == 0 && !isApplying;

    public void Start()
    {
        if (!manager.IsInstalled) return;
        checkTimer.Start();
        _ = CheckAsync();
    }

    public async Task ApplyAsync()
    {
        RefreshActiveJobs();
        if (!CanApply || ready is null) return;

        isApplying = true;
        RaiseChanged();
        logger.Info($"Applying update {ready.Version}.");
        try
        {
            await agent.StopAsync();
            manager.ApplyUpdatesAndRestart(ready);
        }
        catch (Exception exception)
        {
            logger.Error($"Could not apply update {ready.Version}.", exception);
            isApplying = false;
            RaiseChanged();
            await agent.StartAsync();
        }
    }

    private async Task CheckAsync()
    {
        if (isChecking || ready is not null) return;
        isChecking = true;
        try
        {
            if (manager.UpdatePendingRestart is { } pending)
            {
                SetReady(pending);
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return;
            logger.Info($"Downloading update {update.TargetFullRelease.Version}.");
            await manager.DownloadUpdatesAsync(update);
            SetReady(update.TargetFullRelease);
        }
        catch (Exception exception)
        {
            // Offline or rate-limited: try again at the next interval.
            logger.Warning($"Update check failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            isChecking = false;
        }
    }

    private void SetReady(VelopackAsset asset)
    {
        ready = asset;
        checkTimer.Stop();
        jobTimer.Start();
        logger.Info($"Update {asset.Version} is downloaded and ready to install.");
        RefreshActiveJobs();
    }

    private void RefreshActiveJobs()
    {
        activeJobs = agent.ActiveJobCount();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        this.RaisePropertyChanged(nameof(BannerVisible));
        this.RaisePropertyChanged(nameof(BannerText));
        this.RaisePropertyChanged(nameof(CanApply));
    }
}
