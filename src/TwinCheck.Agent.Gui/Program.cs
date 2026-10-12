using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using System;
using System.Linq;
using System.Runtime.Versioning;
using TwinCheck.Agent.Gui.Desktop;
using Velopack;

namespace TwinCheck.Agent.Gui
{
    class Program
    {
        /// <summary>Set on Windows, where the GUI is a tray app that hosts the scan agent API.</summary>
        public static SingleInstance? TrayInstance { get; private set; }
        public static bool StartMinimized { get; private set; }

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Must run first: Velopack invokes the exe with hook arguments during install,
            // update, and uninstall, and applies downloaded updates on startup.
            var velopack = VelopackApp.Build();
            if (OperatingSystem.IsWindows()) velopack = AddWindowsHooks(velopack);
            velopack.Run();

            if (!OperatingSystem.IsWindows())
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return;
            }

            using var instance = SingleInstance.TryAcquire();
            if (instance is null) return;

            LegacyInstall.DetachFromMachinePaths();
            TrayInstance = instance;
            StartMinimized = args.Contains(StartupRegistration.MinimizedArgument);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();

        [SupportedOSPlatform("windows")]
        private static VelopackApp AddWindowsHooks(VelopackApp velopack) => velopack
            .OnAfterInstallFastCallback(_ => StartupRegistration.Register())
            .OnAfterUpdateFastCallback(_ => StartupRegistration.Register())
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartupRegistration.Unregister();
                LocalhostCertificate.RemoveFromPersonalStore();
            });
    }
}
