using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqliteBrowser.App.Views;

/// <summary>A modal dialog used for informational messages, error messages, and yes/no confirmations.</summary>
public partial class MessageDialog : Window
{
    private bool _result;

    public MessageDialog()
    {
        InitializeComponent();
    }

    public static Task ShowInfoAsync(Window? owner, string title, string message) =>
        Configure(title, message, "OK", null).RunAsync(owner);

    public static Task ShowErrorAsync(Window? owner, string title, string message) =>
        Configure(title, message, "OK", null).RunAsync(owner);

    public static async Task<bool> ShowConfirmAsync(Window? owner, string title, string message, string confirmText, string cancelText)
    {
        var dialog = Configure(title, message, confirmText, cancelText);
        await dialog.RunAsync(owner).ConfigureAwait(true);
        return dialog._result;
    }

    private static MessageDialog Configure(string title, string message, string confirmText, string? cancelText)
    {
        var dialog = new MessageDialog
        {
            Title = title,
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        if (cancelText is null)
        {
            dialog.CancelButton.IsVisible = false;
        }
        else
        {
            dialog.CancelButton.Content = cancelText;
        }

        return dialog;
    }

    private async Task RunAsync(Window? owner)
    {
        if (owner is not null && owner.IsVisible)
        {
            await ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var tcs = new TaskCompletionSource();
            Closed += (_, _) => tcs.TrySetResult();
            Show();
            await tcs.Task.ConfigureAwait(true);
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }
}
