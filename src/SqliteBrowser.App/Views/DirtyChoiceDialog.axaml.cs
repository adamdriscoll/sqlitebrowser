using Avalonia.Controls;
using Avalonia.Interactivity;
using SqliteBrowser.App.Models;

namespace SqliteBrowser.App.Views;

/// <summary>A modal 3-way dialog (Save / Discard / Cancel) for closing or reverting a dirty database.</summary>
public partial class DirtyChoiceDialog : Window
{
    private DirtyCloseChoice _result = DirtyCloseChoice.Cancel;

    public DirtyChoiceDialog()
    {
        InitializeComponent();
    }

    public static async Task<DirtyCloseChoice> ShowAsync(Window? owner, string title, string message)
    {
        var dialog = new DirtyChoiceDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;

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

        return dialog._result;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _result = DirtyCloseChoice.SaveAndProceed;
        Close();
    }

    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    {
        _result = DirtyCloseChoice.DiscardAndProceed;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = DirtyCloseChoice.Cancel;
        Close();
    }
}
