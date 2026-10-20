using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TwinCheck.Agent.Gui.Desktop;
using TwinCheck.Agent.Gui.ViewModels;

namespace TwinCheck.Agent.Gui.Views
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer jobsRefreshTimer;

        public MainWindow()
        {
            InitializeComponent();
            jobsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            jobsRefreshTimer.Tick += async (_, _) =>
            {
                if (DataContext is MainWindowViewModel { IsJobsVisible: true } viewModel)
                {
                    await viewModel.RefreshJobsAsync();
                }
                else if (DataContext is MainWindowViewModel { IsUploadsVisible: true } uploadsViewModel)
                {
                    await uploadsViewModel.RefreshUploadsAsync();
                }
            };
            jobsRefreshTimer.Start();
            Closed += (_, _) => jobsRefreshTimer.Stop();
        }

        private async void BrowseSource_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel viewModel) return;
            var selected = await PickFolder("Choose scanner source folder", viewModel.SourceDir);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                viewModel.SourceDir = selected;
            }
        }

        private async void BrowseDestination_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel viewModel) return;
            var selected = await PickFolder("Choose scan destination folder", viewModel.DestinationDir);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                viewModel.DestinationDir = selected;
            }
        }

        private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Save();
                await viewModel.RefreshAsync();
            }
        }

        private async void AgentAction_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (OperatingSystem.IsWindows() && DataContext is MainWindowViewModel { Agent: { } agent } viewModel)
            {
                await agent.RunActionAsync();
                await viewModel.RefreshAsync();
            }
        }

        private async void ApplyUpdate_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (OperatingSystem.IsWindows() && DataContext is MainWindowViewModel { Updater: { } updater })
            {
                await updater.ApplyAsync();
            }
        }

        private async void Refresh_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshAsync();
            }
        }

        private void Reload_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Load();
            }
        }

        private void AddProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AddProfile();
            }
        }

        private void DeleteProfile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.DeleteSelectedProfile();
            }
        }

        private void GenerateApiKey_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.GenerateApiKey();
            }
        }

        private void Overview_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Overview");
        private void Profiles_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Profiles");
        private async void Jobs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Navigate("Jobs");
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshJobsAsync();
            }
        }
        private async void Uploads_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Navigate("Uploads");
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshUploadsAsync();
            }
        }

        private void Diagnostics_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Diagnostics");
        private void Logs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Logs");
        private void Setup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Setup");

        private async void RefreshJobs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshJobsAsync();
            }
        }

        private void CancelJob_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.BeginCancelSelectedJob();
            }
        }

        private async void ConfirmCancelJob_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.ConfirmCancelJobAsync();
            }
        }

        private void KeepJob_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.DismissCancelJob();
            }
        }

        private async void RefreshUploads_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshUploadsAsync();
            }
        }

        private async void PauseResumeUploads_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.TogglePauseUploadsAsync();
            }
        }

        private async void RetryUpload_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RetrySelectedUploadAsync();
            }
        }

        private void OpenUploadLocal_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel { SelectedUpload: { } upload } viewModel)
            {
                viewModel.UploadActionMessage = TryOpenFolder(upload.StagingDir, "The local copy", checkExists: true);
            }
        }

        private void OpenUploadDestination_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel { SelectedUpload: { } upload } viewModel)
            {
                viewModel.UploadActionMessage = TryOpenFolder(upload.FinalDir, "The destination folder", checkExists: false);
            }
        }

        private async void RefreshCleanup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.RefreshCleanupAsync();
            }
        }

        private void OpenCleanup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel { SelectedCleanup: { } entry } viewModel)
            {
                var message = TryOpenFolder(entry.Path, "This folder", checkExists: false);
                if (message.Length > 0) viewModel.CleanupStatus = message;
            }
        }

        private async void DeleteSelectedCleanup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel { SelectedCleanup: { Verified: true } entry } viewModel)
            {
                await ConfirmAndDelete(viewModel, [entry]);
            }
        }

        private async void DeleteAllVerifiedCleanup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await ConfirmAndDelete(viewModel, viewModel.CleanupEntries.Where(entry => entry.Verified).ToArray());
            }
        }

        private async Task ConfirmAndDelete(MainWindowViewModel viewModel, CleanupRow[] entries)
        {
            if (entries.Length == 0) return;
            var size = CleanupRow.FormatBytes(entries.Sum(entry => entry.SizeBytes));
            var what = entries.Length == 1 ? entries[0].Name : $"{entries.Length} folders";
            var confirmed = await ConfirmDialog.ShowAsync(
                this,
                "Delete originals?",
                $"Permanently delete {what} ({size})? Their finished files are verified on the destination. This can't be undone.",
                "Delete",
                "Keep");
            if (confirmed)
            {
                await viewModel.DeleteCleanupAsync(entries);
            }
        }

        /// <summary>Opens a folder in the file manager; returns a message when it can't.</summary>
        /// <param name="checkExists">
        /// Only for local folders. Checking a folder on a hung NAS would freeze this window; the file
        /// manager runs in its own process, so it can wait instead.
        /// </param>
        private static string TryOpenFolder(string folder, string label, bool checkExists)
        {
            if (string.IsNullOrWhiteSpace(folder) || (checkExists && !Directory.Exists(folder)))
            {
                return $"{label} no longer exists: {folder}";
            }

            try
            {
                OpenFolder(folder);
                return "";
            }
            catch (Exception exception)
            {
                return $"Could not open {folder}: {exception.Message}";
            }
        }

        private void Navigate(string page)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Navigate(page);
            }
        }

        private async void CopyDiagnostics_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await SetClipboardText(viewModel.BuildDiagnosticsClipboardText());
                viewModel.StatusMessage = "Copied diagnostics to clipboard.";
            }
        }

        private async void CopyApiKey_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await SetClipboardText(viewModel.ApiKey);
                viewModel.StatusMessage = "Copied API key to clipboard.";
            }
        }

        private void OpenLogs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel viewModel) return;
            Directory.CreateDirectory(viewModel.LogDirectory);
            OpenFolder(viewModel.LogDirectory);
        }

        private async Task SetClipboardText(string text)
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        }

        private static void OpenFolder(string folder)
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", folder);
                return;
            }

            Process.Start("xdg-open", folder);
        }

        private async System.Threading.Tasks.Task<string?> PickFolder(string title, string currentPath)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel is null)
            {
                return null;
            }

            IStorageFolder? suggestedStart = null;
            if (Directory.Exists(currentPath))
            {
                suggestedStart = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(currentPath));
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = suggestedStart
            });

            return folders.FirstOrDefault()?.Path.LocalPath;
        }
    }
}
