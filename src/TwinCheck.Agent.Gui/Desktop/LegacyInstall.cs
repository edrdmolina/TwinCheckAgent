using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Gui.Desktop;

public enum LegacyRemovalOutcome { Removed, Declined, Failed }

public sealed record LegacyRemovalResult(LegacyRemovalOutcome Outcome, string Message);

/// <summary>
/// The pre-tray Windows install: a LocalSystem service in Program Files with shared data in
/// %ProgramData%\TwinCheck\ScanAgent and machine-wide TWINCHECK_AGENT_* variables.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LegacyInstall
{
    public const string ServiceName = "TwinCheck Scan Agent";
    public const string RemovalScriptName = "remove-legacy-install.ps1";
    private const string ServiceKeyPath = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TwinCheckScanAgent";
    private const int ErrorCancelled = 1223;

    private static readonly string[] PathVariables =
    [
        LocalAgentConfigStore.ConfigPathEnvironmentVariable,
        LocalAgentLogger.LogDirectoryEnvironmentVariable,
        ScanOperationStore.StateDirectoryEnvironmentVariable,
    ];

    /// <summary>Where the old service kept its data. Captured before the process variables are cleared.</summary>
    public static string DataDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TwinCheck", "ScanAgent");

    public static bool IsInstalled
    {
        get
        {
            using var service = Registry.LocalMachine.OpenSubKey(ServiceKeyPath);
            using var uninstall = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return service is not null || uninstall is not null;
        }
    }

    /// <summary>
    /// The old installer set machine-wide path variables that this process inherits until the
    /// user signs out. Clear them here so config, logs, and jobs always use %AppData%.
    /// </summary>
    public static void DetachFromMachinePaths()
    {
        var legacyState = Environment.GetEnvironmentVariable(ScanOperationStore.StateDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(legacyState)) DataDirectory = Path.GetFullPath(legacyState);
        foreach (var name in PathVariables) Environment.SetEnvironmentVariable(name, null);
    }

    /// <summary>Running jobs in the old service, or null when it is not answering.</summary>
    public static async Task<int?> ActiveJobCountAsync()
    {
        var config = LocalAgentConfigStore.TryLoad(Path.Combine(DataDirectory, LegacyDataMigrator.ConfigFileName));
        if (config is null) return null;

        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, _, _, _) => message.RequestUri?.IsLoopback == true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:3625/api/scan/operations?limit=1");
            request.Headers.Add("X-Api-Key", config.ApiKey);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return document.RootElement.GetProperty("operations").EnumerateArray()
                .Count(operation => !ScanOperationStatuses.IsTerminal(operation.GetProperty("status").GetString() ?? ""));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Runs the bundled removal script elevated. Windows shows one UAC prompt.</summary>
    public static async Task<LegacyRemovalResult> RemoveAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, RemovalScriptName);
        if (!File.Exists(script))
        {
            return new LegacyRemovalResult(LegacyRemovalOutcome.Failed, $"{RemovalScriptName} is missing from {AppContext.BaseDirectory}.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -DataDir \"{DataDirectory}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return new LegacyRemovalResult(LegacyRemovalOutcome.Failed, "Windows did not start the removal script.");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new LegacyRemovalResult(LegacyRemovalOutcome.Removed, "Removed the old TwinCheck service.")
                : new LegacyRemovalResult(LegacyRemovalOutcome.Failed,
                    $"Removing the old version failed (exit code {process.ExitCode}). See {Path.Combine(DataDirectory, "logs", "legacy-removal.log")}.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return new LegacyRemovalResult(LegacyRemovalOutcome.Declined, "Windows permission was declined.");
        }
    }
}
