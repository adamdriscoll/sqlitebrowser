using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqliteBrowser.App.Views;

/// <summary>A modal raw SQL/DDL editor used for creating and editing schema objects.</summary>
public partial class DdlEditorDialog : Window
{
    private bool _confirmed;

    public DdlEditorDialog()
    {
        InitializeComponent();
    }

    public static async Task<string?> ShowAsync(Window? owner, string title, string initialSql)
    {
        var dialog = new DdlEditorDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.SqlEditor.Text = initialSql;

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

        return dialog._confirmed ? dialog.SqlEditor.Text : null;
    }

    private void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = false;
        Close();
    }
}
