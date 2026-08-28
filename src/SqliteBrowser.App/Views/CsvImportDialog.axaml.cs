using Avalonia.Controls;
using Avalonia.Interactivity;
using SqliteBrowser.App.Models;
using SqliteBrowser.Core.Models;

namespace SqliteBrowser.App.Views;

/// <summary>A modal dialog prompting for CSV import options (destination table, delimiter, header, ...).</summary>
public partial class CsvImportDialog : Window
{
    private bool _confirmed;

    public CsvImportDialog()
    {
        InitializeComponent();
    }

    public static async Task<CsvImportPrompt?> ShowAsync(Window? owner, string suggestedTableName)
    {
        var dialog = new CsvImportDialog();
        dialog.TableNameBox.Text = suggestedTableName;

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

        if (!dialog._confirmed || string.IsNullOrWhiteSpace(dialog.TableNameBox.Text))
        {
            return null;
        }

        char delimiter = string.IsNullOrEmpty(dialog.DelimiterBox.Text) ? ',' : dialog.DelimiterBox.Text[0];
        var options = new CsvImportOptions
        {
            Delimiter = delimiter,
            HasHeader = dialog.HasHeaderCheck.IsChecked ?? true,
            InferTypes = dialog.InferTypesCheck.IsChecked ?? true,
            CreateTable = dialog.CreateTableCheck.IsChecked ?? true,
        };

        return new CsvImportPrompt(dialog.TableNameBox.Text.Trim(), options);
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
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
