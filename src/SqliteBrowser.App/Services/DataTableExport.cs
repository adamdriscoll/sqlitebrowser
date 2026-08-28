using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SqliteBrowser.App.Services;

/// <summary>
/// Writes an already-materialized <see cref="DataTable"/> (e.g. the last Execute SQL result set) to
/// CSV or JSON. Kept separate from <c>SqliteBrowser.Core</c>'s SQL-driven export helpers because
/// re-running arbitrary user SQL just to export its own result set would re-execute any
/// side-effecting statements (INSERT/UPDATE/DELETE) a second time.
/// </summary>
internal static class DataTableExport
{
    public static async Task WriteCsvAsync(DataTable table, string destinationPath, char delimiter, bool includeHeader, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(destinationPath, append: false);

        if (includeHeader)
        {
            await writer.WriteLineAsync(string.Join(delimiter, table.Columns.Cast<DataColumn>().Select(c => FormatField(c.ColumnName, delimiter))).AsMemory(), ct).ConfigureAwait(false);
        }

        foreach (DataRow row in table.Rows)
        {
            ct.ThrowIfCancellationRequested();
            var fields = table.Columns.Cast<DataColumn>().Select(c => FormatField(FormatCell(row[c]), delimiter));
            await writer.WriteLineAsync(string.Join(delimiter, fields).AsMemory(), ct).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task WriteJsonAsync(DataTable table, string destinationPath, CancellationToken ct = default)
    {
        var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            await using var jsonWriter = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            jsonWriter.WriteStartArray();

            foreach (DataRow row in table.Rows)
            {
                ct.ThrowIfCancellationRequested();
                jsonWriter.WriteStartObject();
                foreach (DataColumn column in table.Columns)
                {
                    object value = row[column];
                    if (value is DBNull)
                    {
                        jsonWriter.WriteNull(column.ColumnName);
                        continue;
                    }

                    switch (value)
                    {
                        case long l:
                            jsonWriter.WriteNumber(column.ColumnName, l);
                            break;
                        case int i:
                            jsonWriter.WriteNumber(column.ColumnName, i);
                            break;
                        case double d:
                            jsonWriter.WriteNumber(column.ColumnName, d);
                            break;
                        case byte[] bytes:
                            jsonWriter.WriteString(column.ColumnName, Convert.ToBase64String(bytes));
                            break;
                        default:
                            jsonWriter.WriteString(column.ColumnName, Convert.ToString(value, CultureInfo.InvariantCulture));
                            break;
                    }
                }

                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray();
            await jsonWriter.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static string? FormatCell(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string FormatField(string? value, char delimiter)
    {
        value ??= string.Empty;
        bool needsQuoting = value.IndexOf(delimiter) >= 0 || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
        if (!needsQuoting)
        {
            return value;
        }

        var sb = new StringBuilder();
        sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        return sb.ToString();
    }
}
