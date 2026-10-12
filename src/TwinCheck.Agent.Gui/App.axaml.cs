using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using TwinCheck.Agent.Gui.Desktop;
using TwinCheck.Agent.Gui.ViewModels;
using TwinCheck.Agent.Gui.Views;

namespace TwinCheck.Agent.Gui
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (OperatingSystem.IsWindows() && Program.TrayInstance is { } instance)
                {
                    new TrayApp(this, desktop, instance).Start(Program.StartMinimized);
                }
                else
                {
                    desktop.MainWindow = new MainWindow
                    {
                        DataContext = new MainWindowViewModel(),
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Windows: the window hides to the tray instead of closing, and the scan agent API runs
        /// in this process until the operator chooses Quit or signs out.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private sealed class TrayApp(Application application, IClassicDesktopStyleApplicationLifetime desktop, SingleInstance instance)
        {
            private readonly DesktopAgent agent = new();
            private AppUpdater? updater;
            private MainWindowViewModel? viewModel;
            private MainWindow? window;
            private bool quitting;

            public void Start(bool minimized)
            {
                DesktopAgent.PrepareConfig();
                updater = new AppUpdater(agent);
                viewModel = new MainWindowViewModel(agent, updater);
                agent.ConfigChanged += viewModel.Load;

                window = new MainWindow { DataContext = viewModel };
                window.Closing += (_, e) =>
                {
                    if (quitting || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
                    e.Cancel = true;
                    window.Hide();
                };
                desktop.Exit += (_, _) => Task.Run(agent.StopAsync).Wait(TimeSpan.FromSeconds(10));

                CreateTrayIcon();
                instance.ListenForShowRequests(() => Dispatcher.UIThread.Post(ShowWindow));
                if (!minimized) ShowWindow();
                _ = StartAgentAsync();
            }

            private async Task StartAgentAsync()
            {
                await agent.StartAsync();
                updater!.Start();
                await viewModel!.RefreshAsync();
            }

            private void CreateTrayIcon()
            {
                var open = new NativeMenuItem("Open TwinCheck Scan Agent");
                open.Click += (_, _) => ShowWindow();
                var quit = new NativeMenuItem("Quit");
                quit.Click += async (_, _) => await QuitAsync();

                var tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TwinCheck.Agent.Gui/Assets/avalonia-logo.ico"))),
                    ToolTipText = "TwinCheck Scan Agent",
                    Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } },
                };
                tray.Clicked += (_, _) => ShowWindow();
                TrayIcon.SetIcons(application, [tray]);
            }

            private void ShowWindow()
            {
                if (window is null) return;
                window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }

            private async Task QuitAsync()
            {
                var activeJobs = agent.ActiveJobCount();
                if (activeJobs > 0)
                {
                    ShowWindow();
                    var confirmed = await ConfirmDialog.ShowAsync(window!,
                        "Quit TwinCheck Scan Agent?",
                        $"{activeJobs} scan job(s) are still running. Quitting pauses them, and TwinCheckN can't reach the agent until it starts again. " +
                        "The jobs resume when the app next starts.",
                        "Quit anyway", "Keep running");
                    if (!confirmed) return;
                }

                quitting = true;
                await agent.StopAsync();
                desktop.Shutdown();
            }
        }
    }
}
