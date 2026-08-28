using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace SqliteBrowser.App.Converters;

/// <summary>
/// Formats DataGrid cell values for display without changing how they round-trip when edited:
/// <c>DBNull</c>/<c>null</c> renders as the literal text "NULL", and <c>byte[]</c> (BLOB) values
/// render as a size summary rather than a numeric type name. Any other value passes through
/// unchanged, and editing simply writes the typed text straight back (SQLite's dynamic typing
/// handles the rest when the edit is persisted).
/// </summary>
public sealed partial class CellDisplayConverter : IValueConverter
{
    public static readonly CellDisplayConverter Instance = new();

    [GeneratedRegex(@"^<BLOB \d+ bytes>$")]
    private static partial Regex BlobPlaceholderPattern();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => $"<BLOB {bytes.Length} bytes>",
        _ => value,
    };

    /// <summary>
    /// Passes typed edits straight back to the underlying cell, with one guard: our own
    /// "&lt;BLOB n bytes&gt;" display placeholder is never round-tripped back verbatim (e.g. a grid
    /// cell entering and leaving edit mode without an actual change). Declared BLOB columns are also
    /// made read-only in the grid (see <c>MainWindow.axaml.cs</c>) so this is a defense-in-depth
    /// backstop rather than the primary protection.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && BlobPlaceholderPattern().IsMatch(text)
            ? BindingOperations.DoNothing
            : value;
}
