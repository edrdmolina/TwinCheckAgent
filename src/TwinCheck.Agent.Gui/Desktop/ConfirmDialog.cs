using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TwinCheck.Agent.Gui.Desktop;

/// <summary>A minimal two-button confirmation window.</summary>
public static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirm, string cancel)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirmButton = new Button { Content = confirm, Classes = { "primary" } };
        var cancelButton = new Button { Content = cancel, IsDefault = true, IsCancel = true };
        confirmButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, confirmButton },
                },
            },
        };

        return dialog.ShowDialog<bool>(owner);
    }
}
