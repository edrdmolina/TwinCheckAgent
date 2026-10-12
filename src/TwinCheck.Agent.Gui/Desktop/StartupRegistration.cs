using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TwinCheck.Agent.Gui.Desktop;

/// <summary>Starts the tray app, minimized, when the Windows user signs in.</summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    public const string MinimizedArgument = "--minimized";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TwinCheckScanAgent";

    public static void Register()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{exePath}\" {MinimizedArgument}");
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
