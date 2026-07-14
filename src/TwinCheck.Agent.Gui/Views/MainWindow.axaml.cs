using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TwinCheck.Agent.Gui.ViewModels;

namespace TwinCheck.Agent.Gui.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
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

        private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Save();
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
        private void Diagnostics_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Diagnostics");
        private void Logs_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Logs");
        private void Setup_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate("Setup");

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
