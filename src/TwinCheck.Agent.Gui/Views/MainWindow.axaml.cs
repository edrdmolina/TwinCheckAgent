using Avalonia.Controls;
using System.IO;
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

        private void Reload_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Load();
            }
        }

        private async System.Threading.Tasks.Task<string?> PickFolder(string title, string currentPath)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                Directory = Directory.Exists(currentPath) ? currentPath : null,
            };

            return await dialog.ShowAsync(this);
        }
    }
}
