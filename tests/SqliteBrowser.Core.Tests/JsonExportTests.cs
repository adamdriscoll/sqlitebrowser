using System.Text;
using System.Text.Json;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class JsonExportTests
{
    [Fact]
    public async Task ExportJsonAsync_WritesArrayOfObjectsWithCorrectTypes()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            "CREATE TABLE t (id INTEGER, name TEXT, score REAL, tag TEXT); " +
            "INSERT INTO t VALUES (1, 'Alice', 9.5, NULL);");

        using var stream = new MemoryStream();
        int rows = await session.ExportJsonAsync("SELECT id, name, score, tag FROM t;", stream);
        Assert.Equal(1, rows);

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        var element = root[0];

        Assert.Equal(1, element.GetProperty("id").GetInt64());
        Assert.Equal("Alice", element.GetProperty("name").GetString());
        Assert.Equal(9.5, element.GetProperty("score").GetDouble());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("tag").ValueKind);
    }

    [Fact]
    public async Task ExportJsonAsync_EmptyResultSet_WritesEmptyArray()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");

        using var stream = new MemoryStream();
        int rows = await session.ExportJsonAsync("SELECT id FROM t;", stream);
        Assert.Equal(0, rows);

        stream.Position = 0;
        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("[]", text);
    }

    [Fact]
    public async Task ExportJsonAsync_BlobIsBase64EncodedString()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (data BLOB);");
        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var row = page.Data.NewRow();
        row["data"] = new byte[] { 10, 20, 30 };
        page.Data.Rows.Add(row);
        await session.ApplyChangesAsync(page);

        using var stream = new MemoryStream();
        await session.ExportJsonAsync("SELECT data FROM t;", stream);

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        string base64 = doc.RootElement[0].GetProperty("data").GetString()!;
        Assert.Equal(new byte[] { 10, 20, 30 }, Convert.FromBase64String(base64));
    }

    [Fact]
    public async Task ExportJsonFileAsync_WritesToDisk()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1), (2);");

        using var file = new TestSupport.TempFile(".json");
        int rows = await session.ExportJsonFileAsync("SELECT id FROM t ORDER BY id;", file.Path);
        Assert.Equal(2, rows);

        string content = await File.ReadAllTextAsync(file.Path);
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }
}
