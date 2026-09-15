using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SqliteBrowser.App.Models;
using System.Text.Json;

namespace SqliteBrowser.App.Views;

/// <summary>A typed cell inspector/editor with JSON, hexadecimal BLOB, and image-preview tools.</summary>
public partial class ValueEditorDialog : Window
{
    private static readonly FilePickerFileType AllFilesType = new("All Files") { Patterns = ["*"] };
    private readonly ValueEditorDocument _document;
    private ValueEditResult? _result;
    private Bitmap? _previewBitmap;

    public ValueEditorDialog()
        : this(DBNull.Value, false)
    {
    }

    private ValueEditorDialog(object? initialValue, bool isReadOnly)
    {
        InitializeComponent();
        _document = new ValueEditorDocument(initialValue);

        StorageClassSelector.ItemsSource = new[] { "NULL", "INTEGER", "REAL", "TEXT", "BLOB" };
        StorageClassSelector.SelectedIndex = (int)_document.StorageClass;
        TextEditor.Text = _document.Text;
        HexEditor.Text = _document.HexText;
        StorageClassSelector.IsEnabled = !isReadOnly;
        TextEditor.IsReadOnly = isReadOnly;
        HexEditor.IsReadOnly = isReadOnly;
        ImportBlobButton.IsEnabled = !isReadOnly;
        SaveButton.IsVisible = !isReadOnly;
        CancelButton.Content = isReadOnly ? "Close" : "Cancel";
        ReadOnlyText.IsVisible = isReadOnly;
        UpdateEditorVisibility();
        UpdateImagePreview();
    }

    public static async Task<ValueEditResult?> ShowAsync(Window? owner, string title, object? initialValue, bool isReadOnly)
    {
        var dialog = new ValueEditorDialog(initialValue, isReadOnly) { Title = title };

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

    protected override void OnClosed(EventArgs e)
    {
        _previewBitmap?.Dispose();
        base.OnClosed(e);
    }

    private void OnStorageClassChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (StorageClassSelector.SelectedIndex >= 0)
        {
            _document.StorageClass = (SqliteStorageClass)StorageClassSelector.SelectedIndex;
            StatusText.Text = string.Empty;
            UpdateEditorVisibility();
        }
    }

    private void UpdateEditorVisibility()
    {
        bool isNull = _document.StorageClass == SqliteStorageClass.Null;
        bool isBlob = _document.StorageClass == SqliteStorageClass.Blob;
        NullEditor.IsVisible = isNull;
        BlobEditor.IsVisible = isBlob;
        TextEditor.IsVisible = !isNull && !isBlob;
        JsonTools.IsVisible = _document.StorageClass == SqliteStorageClass.Text;
    }

    private void OnFormatJsonClick(object? sender, RoutedEventArgs e) => TransformJson(indented: true);

    private void OnMinifyJsonClick(object? sender, RoutedEventArgs e) => TransformJson(indented: false);

    private void OnValidateJsonClick(object? sender, RoutedEventArgs e)
    {
        _document.Text = TextEditor.Text ?? string.Empty;
        try
        {
            _ = _document.FormatJson(indented: false);
            SetStatus("Valid JSON.", isError: false);
        }
        catch (JsonException ex)
        {
            SetStatus($"Invalid JSON: {ex.Message}", isError: true);
        }
    }

    private void TransformJson(bool indented)
    {
        _document.Text = TextEditor.Text ?? string.Empty;
        try
        {
            TextEditor.Text = _document.FormatJson(indented);
            SetStatus(indented ? "JSON formatted." : "JSON minified.", isError: false);
        }
        catch (JsonException ex)
        {
            SetStatus($"Invalid JSON: {ex.Message}", isError: true);
        }
    }

    private async void OnImportBlobClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import BLOB Value",
            AllowMultiple = false,
            FileTypeFilter = [AllFilesType],
        }).ConfigureAwait(true);

        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            _document.ImportBlob(bytes);
            HexEditor.Text = _document.HexText;
            SetStatus($"Imported {bytes.Length} byte(s).", isError: false);
            UpdateImagePreview();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not import BLOB: {ex.Message}", isError: true);
        }
    }

    private async void OnExportBlobClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _document.HexText = HexEditor.Text ?? string.Empty;
            byte[] bytes = _document.GetBlobBytes();
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export BLOB Value",
                SuggestedFileName = "value.bin",
                FileTypeChoices = [AllFilesType],
            }).ConfigureAwait(true);

            string? path = file?.TryGetLocalPath();
            if (path is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            SetStatus($"Exported {bytes.Length} byte(s).", isError: false);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not export BLOB: {ex.Message}", isError: true);
        }
    }

    private void OnHexTextChanged(object? sender, TextChangedEventArgs e) => UpdateImagePreview();

    private void UpdateImagePreview()
    {
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        ImagePreview.Source = null;
        ImagePreview.IsVisible = false;
        ImagePlaceholder.IsVisible = true;

        try
        {
            _document.HexText = HexEditor.Text ?? string.Empty;
            byte[] bytes = _document.GetBlobBytes();
            if (!ValueEditorDocument.IsSupportedImage(bytes))
            {
                return;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            _previewBitmap = new Bitmap(stream);
            ImagePreview.Source = _previewBitmap;
            ImagePreview.IsVisible = true;
            ImagePlaceholder.IsVisible = false;
        }
        catch (FormatException)
        {
            // A partially typed hex value simply has no preview.
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetStatus($"Image preview unavailable: {ex.Message}", isError: true);
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _document.Text = TextEditor.Text ?? string.Empty;
        _document.HexText = HexEditor.Text ?? string.Empty;
        try
        {
            _result = new ValueEditResult(_document.BuildValue());
            Close();
        }
        catch (FormatException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? Brushes.Red : Brushes.Green;
    }
}
