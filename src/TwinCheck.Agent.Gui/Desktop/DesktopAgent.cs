using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using TwinCheck.Agent.Core;
using TwinCheck.Agent.Hosting;

namespace TwinCheck.Agent.Gui.Desktop;

/// <summary>
/// Runs the scan agent API inside the Windows tray app and reports its state to the GUI
/// as a banner with at most one action button.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopAgent : ReactiveObject
{
    private enum PendingAction { None, RemoveLegacy, TrustCertificate, Retry }

    private readonly LocalAgentLogger logger = new();
    private WebApplication? app;
    private X509Certificate2? certificate;
    private PendingAction pendingAction;
    private string bannerText = "";
    private string actionLabel = "";
    private bool isBusy;

    /// <summary>Raised after the old install's config is copied in, so the GUI reloads it.</summary>
    public event Action? ConfigChanged;

    public string BannerText
    {
        get => bannerText;
        private set
        {
            this.RaiseAndSetIfChanged(ref bannerText, value);
            this.RaisePropertyChanged(nameof(BannerVisible));
        }
    }

    public bool BannerVisible => !string.IsNullOrEmpty(BannerText);

    public string ActionLabel
    {
        get => actionLabel;
        private set
        {
            this.RaiseAndSetIfChanged(ref actionLabel, value);
            this.RaisePropertyChanged(nameof(ActionVisible));
        }
    }

    public bool ActionVisible => !string.IsNullOrEmpty(ActionLabel);

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref isBusy, value);
            this.RaisePropertyChanged(nameof(ActionEnabled));
        }
    }

    public bool ActionEnabled => !IsBusy;

    public bool IsRunning => app is not null;

    /// <summary>
    /// Call before the GUI first reads the config. While the old service is still installed
    /// the config is left alone, so its data can be copied in after removal.
    /// </summary>
    public static void PrepareConfig()
    {
        if (!LegacyInstall.IsInstalled) MigrateLegacyDataAndCreateConfig();
    }

    public async Task StartAsync()
    {
        if (LegacyInstall.IsInstalled)
        {
            Show("An older TwinCheck Scan Agent (Windows service) is still installed and must be removed before this app can start. " +
                 "Windows will ask for permission. Profiles, the API key, and job history are kept.",
                 "Remove old version", PendingAction.RemoveLegacy);
            return;
        }

        await StartHostAsync();
    }

    public async Task RunActionAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            switch (pendingAction)
            {
                case PendingAction.RemoveLegacy:
                    await RemoveLegacyAsync();
                    break;
                case PendingAction.TrustCertificate when certificate is not null:
                    var trusted = await Task.Run(() => LocalhostCertificate.EnsureTrusted(certificate));
                    if (trusted) Clear();
                    break;
                case PendingAction.Retry:
                    await StartAsync();
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public int ActiveJobCount() =>
        app?.Services.GetRequiredService<ScanOperationService>().ListRecent(1)
            .Count(job => !ScanOperationStatuses.IsTerminal(job.Status)) ?? 0;

    public async Task StopAsync()
    {
        if (app is null) return;
        var running = app;
        app = null;
        this.RaisePropertyChanged(nameof(IsRunning));
        await running.StopAsync();
        await running.DisposeAsync();
        logger.Info("Stopped the embedded scan agent.");
    }

    private async Task StartHostAsync()
    {
        if (app is not null) return;
        WebApplication? starting = null;
        try
        {
            var result = await Task.Run(LocalhostCertificate.GetOrCreate);
            certificate = result.Certificate;
            starting = AgentWebHost.Create([], new AgentHostOptions
            {
                Certificate = certificate,
                ContentRootPath = AppContext.BaseDirectory,
            });
            await starting.StartAsync();
            app = starting;
            this.RaisePropertyChanged(nameof(IsRunning));
            logger.Info($"Started the embedded scan agent {AgentVersion.Current} on https://localhost:{AgentHostOptions.DefaultPort}.");

            if (result.Trusted)
            {
                Clear();
            }
            else
            {
                Show("The agent is running, but its HTTPS certificate isn't trusted yet, so the browser will show a warning. " +
                     "Click Trust certificate and choose Yes in the Windows prompt.",
                     "Trust certificate", PendingAction.TrustCertificate);
            }
        }
        catch (IOException exception)
        {
            await DisposeQuietly(starting);
            logger.Error("The embedded scan agent could not bind its port.", exception);
            Show($"Port {AgentHostOptions.DefaultPort} is already in use by another program, so the agent can't start. " +
                 $"Close that program, then click Retry. ({exception.Message})",
                 "Retry", PendingAction.Retry);
        }
        catch (Exception exception)
        {
            await DisposeQuietly(starting);
            logger.Error("The embedded scan agent failed to start.", exception);
            Show($"The agent failed to start: {exception.Message}", "Retry", PendingAction.Retry);
        }
    }

    private async Task RemoveLegacyAsync()
    {
        BannerText = "Checking the old service for running jobs...";
        var activeJobs = await LegacyInstall.ActiveJobCountAsync();
        if (activeJobs > 0)
        {
            Show($"The old service still has {activeJobs} job(s) running. Let them finish, then click Remove old version.",
                 "Remove old version", PendingAction.RemoveLegacy);
            return;
        }

        BannerText = "Removing the old version. Choose Yes in the Windows prompt...";
        var removal = await LegacyInstall.RemoveAsync();
        logger.Info($"Legacy service removal: {removal.Outcome}. {removal.Message}");
        if (removal.Outcome != LegacyRemovalOutcome.Removed)
        {
            Show($"{removal.Message} The agent can't start until the old version is removed.",
                 "Remove old version", PendingAction.RemoveLegacy);
            return;
        }

        await Task.Run(MigrateLegacyDataAndCreateConfig);
        ConfigChanged?.Invoke();
        await StartHostAsync();
    }

    private static void MigrateLegacyDataAndCreateConfig()
    {
        var targetDir = Path.GetDirectoryName(LocalAgentConfigStore.ConfigPath)!;
        var migration = LegacyDataMigrator.Migrate(LegacyInstall.DataDirectory, targetDir);
        if (migration.Outcome == LegacyMigrationOutcome.Migrated)
        {
            new LocalAgentLogger().Info($"Copied {migration.FilesCopied} file(s) from {LegacyInstall.DataDirectory} to {targetDir}.");
        }

        if (LocalAgentConfigStore.CreateIfMissing())
        {
            new LocalAgentLogger().Info($"Created {LocalAgentConfigStore.ConfigPath} with a new API key.");
        }
    }

    private void Show(string text, string action, PendingAction next)
    {
        BannerText = text;
        ActionLabel = action;
        pendingAction = next;
    }

    private void Clear() => Show("", "", PendingAction.None);

    private static async Task DisposeQuietly(WebApplication? application)
    {
        if (application is null) return;
        try { await application.DisposeAsync(); } catch { }
    }
}
