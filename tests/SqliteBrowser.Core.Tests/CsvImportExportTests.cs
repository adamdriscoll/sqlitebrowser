using SqliteBrowser.Core.Models;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class CsvImportExportTests
{
    [Fact]
    public async Task ImportCsvAsync_HeaderWithTypeInference_CreatesTableAndImportsRows()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        const string csv = "id,name,score\n1,Alice,9.5\n2,Bob,7\n";
        using var reader = new StringReader(csv);

        var result = await session.ImportCsvAsync("main", "people", reader, new CsvImportOptions());

        Assert.True(result.TableCreated);
        Assert.Equal(2, result.RowsImported);
        Assert.Equal(new[] { "id", "name", "score" }, result.Columns);

        var columns = await session.GetColumnsAsync("main", "people");
        Assert.Equal("INTEGER", columns.Single(c => c.Name == "id").DataType);
        Assert.Equal("TEXT", columns.Single(c => c.Name == "name").DataType);
        Assert.Equal("REAL", columns.Single(c => c.Name == "score").DataType);

        var rows = await session.ExecuteSqlAsync("SELECT * FROM people ORDER BY id;");
        Assert.Equal(2, rows.Data!.Rows.Count);
        Assert.Equal("Alice", rows.Data.Rows[0]["name"]);
    }

    [Fact]
    public async Task ImportCsvAsync_NoHeader_UsesGeneratedColumnNames()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        const string csv = "1,a\n2,b\n";
        using var reader = new StringReader(csv);

        var result = await session.ImportCsvAsync("main", "t", reader, new CsvImportOptions { HasHeader = false });

        Assert.Equal(new[] { "column1", "column2" }, result.Columns);
        Assert.Equal(2, result.RowsImported);
    }

    [Fact]
    public async Task ImportCsvAsync_QuotedFieldsWithEmbeddedDelimiterNewlineAndEscapedQuotes()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        const string csv = "name,note\r\n" +
                            "\"Smith, John\",\"Line1\nLine2\"\r\n" +
                            "\"She said \"\"hi\"\"\",plain\r\n";
        using var reader = new StringReader(csv);

        var result = await session.ImportCsvAsync("main", "t", reader, new CsvImportOptions());
        Assert.Equal(2, result.RowsImported);

        var rows = await session.ExecuteSqlAsync("SELECT name, note FROM t ORDER BY rowid;");
        Assert.Equal("Smith, John", rows.Data!.Rows[0]["name"]);
        Assert.Equal("Line1\nLine2", rows.Data.Rows[0]["note"]);
        Assert.Equal("She said \"hi\"", rows.Data.Rows[1]["name"]);
    }

    [Fact]
    public async Task ImportCsvAsync_EmptyFieldsBecomeNull()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        const string csv = "a,b\n1,\n,2\n";
        using var reader = new StringReader(csv);

        await session.ImportCsvAsync("main", "t", reader, new CsvImportOptions { InferTypes = false });

        var rows = await session.ExecuteSqlAsync("SELECT a, b FROM t ORDER BY rowid;");
        Assert.Equal(DBNull.Value, rows.Data!.Rows[0]["b"]);
        Assert.Equal(DBNull.Value, rows.Data.Rows[1]["a"]);
    }

    [Fact]
    public async Task ImportCsvAsync_CustomDelimiter_ParsesCorrectly()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        const string csv = "a;b\n1;2\n3;4\n";
        using var reader = new StringReader(csv);

        var result = await session.ImportCsvAsync("main", "t", reader, new CsvImportOptions { Delimiter = ';' });
        Assert.Equal(2, result.RowsImported);
    }

    [Fact]
    public async Task ImportCsvAsync_TableDoesNotExistAndCreateTableFalse_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        using var reader = new StringReader("a,b\n1,2\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ImportCsvAsync("main", "missing", reader, new CsvImportOptions { CreateTable = false }));
    }

    [Fact]
    public async Task ImportCsvAsync_ExistingTable_AppendsRows()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (a INTEGER, b TEXT); INSERT INTO t VALUES (0, 'seed');");

        using var reader = new StringReader("a,b\n1,x\n2,y\n");
        var result = await session.ImportCsvAsync("main", "t", reader, new CsvImportOptions());

        Assert.False(result.TableCreated);
        Assert.Equal(2, result.RowsImported);

        var count = await session.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(3L, count.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task ExportCsvAsync_RoundTripsThroughImport()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            "CREATE TABLE t (id INTEGER, name TEXT); INSERT INTO t VALUES (1, 'Smith, John'), (2, NULL);");

        var writer = new StringWriter();
        int rows = await session.ExportCsvAsync("SELECT id, name FROM t ORDER BY id;", writer, new CsvExportOptions());
        Assert.Equal(2, rows);

        string csv = writer.ToString();
        Assert.Contains("id,name", csv);
        Assert.Contains("\"Smith, John\"", csv);

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        using var csvReader = new StringReader(csv);
        var result = await target.ImportCsvAsync("main", "t2", csvReader, new CsvImportOptions());
        Assert.Equal(2, result.RowsImported);

        var imported = await target.ExecuteSqlAsync("SELECT name FROM t2 ORDER BY id;");
        Assert.Equal("Smith, John", imported.Data!.Rows[0]["name"]);
    }

    [Fact]
    public async Task ExportCsvAsync_WithoutHeader_OmitsHeaderRow()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER); INSERT INTO t VALUES (1);");

        var writer = new StringWriter();
        await session.ExportCsvAsync("SELECT n FROM t;", writer, new CsvExportOptions { IncludeHeader = false });

        Assert.Equal("1", writer.ToString().Trim());
    }

    [Fact]
    public async Task ExportCsvAsync_BlobIsBase64Encoded()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (data BLOB);");
        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var row = page.Data.NewRow();
        row["data"] = new byte[] { 1, 2, 3 };
        page.Data.Rows.Add(row);
        await session.ApplyChangesAsync(page);

        var writer = new StringWriter();
        await session.ExportCsvAsync("SELECT data FROM t;", writer, new CsvExportOptions());

        Assert.Contains(Convert.ToBase64String([1, 2, 3]), writer.ToString());
    }

    [Fact]
    public async Task ImportCsvFileAsync_And_ExportCsvFileAsync_RoundTripThroughDisk()
    {
        using var csvFile = new TestSupport.TempFile(".csv");
        await File.WriteAllTextAsync(csvFile.Path, "a,b\n1,x\n2,y\n");

        await using var session = await DatabaseSession.OpenInMemoryAsync();
        var importResult = await session.ImportCsvFileAsync("main", "t", csvFile.Path, new CsvImportOptions());
        Assert.Equal(2, importResult.RowsImported);

        using var exportFile = new TestSupport.TempFile(".csv");
        int rows = await session.ExportCsvFileAsync("SELECT a, b FROM t ORDER BY a;", exportFile.Path, new CsvExportOptions());
        Assert.Equal(2, rows);
        string content = await File.ReadAllTextAsync(exportFile.Path);
        Assert.Contains("a,b", content);
    }
}
