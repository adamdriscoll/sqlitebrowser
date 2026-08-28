using System.Text;

namespace SqliteBrowser.Core.Services;

/// <summary>
/// Minimal, dependency-free RFC 4180 CSV reader/writer: supports quoted fields, embedded delimiters,
/// embedded CR/LF, and doubled-quote escaping (<c>""</c> inside a quoted field represents a literal quote).
/// </summary>
internal static class CsvFormat
{
    /// <summary>Reads CSV records from <paramref name="reader"/>, one string array per record.</summary>
    public static IEnumerable<string[]> ReadRecords(TextReader reader, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool fieldHadContent = false;
        bool recordHasContent = false;

        int current;
        while ((current = reader.Read()) != -1)
        {
            char c = (char)current;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !fieldHadContent:
                    inQuotes = true;
                    fieldHadContent = true;
                    recordHasContent = true;
                    break;
                case var _ when c == delimiter:
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldHadContent = false;
                    recordHasContent = true;
                    break;
                case '\r':
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    goto case '\n';
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    fieldHadContent = false;
                    yield return fields.ToArray();
                    fields.Clear();
                    recordHasContent = false;
                    break;
                default:
                    field.Append(c);
                    fieldHadContent = true;
                    recordHasContent = true;
                    break;
            }
        }

        if (recordHasContent || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    /// <summary>Formats a single field, quoting it (and doubling internal quotes) only when required.</summary>
    public static string FormatField(string? value, char delimiter)
    {
        value ??= string.Empty;
        bool needsQuoting = value.IndexOf(delimiter) >= 0
            || value.Contains('"')
            || value.Contains('\r')
            || value.Contains('\n');

        if (!needsQuoting)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static async Task WriteRecordAsync(TextWriter writer, IReadOnlyList<string?> fields, char delimiter, CancellationToken ct)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                await writer.WriteAsync(delimiter).ConfigureAwait(false);
            }

            await writer.WriteAsync(FormatField(fields[i], delimiter).AsMemory(), ct).ConfigureAwait(false);
        }

        await writer.WriteAsync('\n').ConfigureAwait(false);
    }
}
