using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SqliteBrowser.App.Models;

/// <summary>
/// Storage-class-aware state and validation for the rich value editor. Conversions happen only
/// when the user explicitly selects a different storage class.
/// </summary>
public sealed class ValueEditorDocument
{
    public ValueEditorDocument(object? value)
    {
        StorageClass = GetStorageClass(value);

        if (value is byte[] bytes)
        {
            HexText = Convert.ToHexString(bytes);
        }
        else if (value is not null and not DBNull)
        {
            Text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    public SqliteStorageClass StorageClass { get; set; }

    public string Text { get; set; } = string.Empty;

    public string HexText { get; set; } = string.Empty;

    public object BuildValue() => StorageClass switch
    {
        SqliteStorageClass.Null => DBNull.Value,
        SqliteStorageClass.Integer => ParseInteger(Text),
        SqliteStorageClass.Real => ParseReal(Text),
        SqliteStorageClass.Text => Text,
        SqliteStorageClass.Blob => ParseHex(HexText),
        _ => throw new InvalidOperationException($"Unsupported SQLite storage class: {StorageClass}."),
    };

    public string FormatJson(bool indented)
    {
        using var document = JsonDocument.Parse(Text);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
        }))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void ImportBlob(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        StorageClass = SqliteStorageClass.Blob;
        HexText = Convert.ToHexString(bytes);
    }

    public byte[] GetBlobBytes() => ParseHex(HexText);

    public static bool IsSupportedImage(ReadOnlySpan<byte> bytes) =>
        IsPng(bytes) || IsJpeg(bytes) || IsGif(bytes) || IsBmp(bytes) || IsWebP(bytes);

    private static SqliteStorageClass GetStorageClass(object? value) => value switch
    {
        null or DBNull => SqliteStorageClass.Null,
        byte[] => SqliteStorageClass.Blob,
        sbyte or byte or short or ushort or int or uint or long or ulong => SqliteStorageClass.Integer,
        float or double or decimal => SqliteStorageClass.Real,
        _ => SqliteStorageClass.Text,
    };

    private static long ParseInteger(string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        throw new FormatException("Enter a signed 64-bit integer.");
    }

    private static double ParseReal(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value))
        {
            return value;
        }

        throw new FormatException("Enter a finite real number using invariant notation.");
    }

    private static byte[] ParseHex(string text)
    {
        string compact = new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length % 2 != 0)
        {
            throw new FormatException("Hexadecimal BLOB data must contain an even number of digits.");
        }

        try
        {
            return Convert.FromHexString(compact);
        }
        catch (FormatException)
        {
            throw new FormatException("BLOB data contains a non-hexadecimal character.");
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF });

    private static bool IsGif(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8);

    private static bool IsBmp(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("BM"u8);

    private static bool IsWebP(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);
}
