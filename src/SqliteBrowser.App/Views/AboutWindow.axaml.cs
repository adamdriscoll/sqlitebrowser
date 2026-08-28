using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqliteBrowser.App.Views;

/// <summary>The About dialog: product description, the Ahtola experimental-provider warning, and the dual MPL-2.0/GPL-3.0-or-later license notice.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    public static async Task ShowAsync(Window? owner)
    {
        var dialog = new AboutWindow();
        if (owner is not null && owner.IsVisible)
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var tcs = new TaskCompletionSource();
            dialog.Closed += (_, _) => tcs.TrySetResult();
            dialog.Show();
            await tcs.Task.ConfigureAwait(true);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
