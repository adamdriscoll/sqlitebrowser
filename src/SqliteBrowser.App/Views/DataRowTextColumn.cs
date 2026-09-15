using System.Data;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using SqliteBrowser.App.Converters;

namespace SqliteBrowser.App.Views;

internal sealed class DataRowTextColumn(string columnName) : DataGridColumn
{
    private DataRowView? _editingRow;
    private TextBox? _editingTextBox;
    private object? _originalValue;
    private bool _editCancelled;

    protected override Control GenerateElement(DataGridCell cell, object dataItem) =>
        new TextBlock
        {
            Name = "CellTextBlock",
            Text = dataItem is DataRowView row ? Format(row[columnName]) : null,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4, 0),
        };

    protected override Control GenerateEditingElement(
        DataGridCell cell,
        object dataItem,
        out BindingExpressionBase? binding)
    {
        binding = null;
        _editingRow = dataItem as DataRowView;
        _originalValue = _editingRow?[columnName];
        _editCancelled = false;
        _editingTextBox = new TextBox
        {
            Name = "CellTextBox",
            Text = Format(_originalValue),
        };

        return _editingTextBox;
    }

    protected override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs editingEventArgs)
    {
        if (editingElement is TextBox textBox)
        {
            textBox.SelectAll();
        }

        return _originalValue;
    }

    protected override void CancelCellEdit(Control editingElement, object? uneditedValue) =>
        _editCancelled = true;

    protected override void EndCellEdit()
    {
        if (!_editCancelled && _editingRow is not null && _editingTextBox is not null)
        {
            string newText = _editingTextBox.Text ?? string.Empty;
            if (!string.Equals(newText, Format(_originalValue), StringComparison.Ordinal))
            {
                object? converted = CellDisplayConverter.Instance.ConvertBack(
                    newText,
                    typeof(object),
                    null,
                    CultureInfo.CurrentCulture);

                if (!ReferenceEquals(converted, BindingOperations.DoNothing))
                {
                    _editingRow[columnName] = converted ?? DBNull.Value;
                }
            }
        }

        _editingRow = null;
        _editingTextBox = null;
        _originalValue = null;
        _editCancelled = false;
        base.EndCellEdit();
    }

    private static string? Format(object? value) =>
        CellDisplayConverter.Instance.Convert(
            value,
            typeof(string),
            null,
            CultureInfo.CurrentCulture)?.ToString();
}
