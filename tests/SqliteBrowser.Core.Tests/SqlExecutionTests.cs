using SqliteBrowser.Core.Exceptions;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class SqlExecutionTests
{
    [Fact]
    public async Task ExecuteSqlAsync_Select_ReturnsDataAndElapsed()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER, name TEXT); INSERT INTO t VALUES (1,'a'), (2,'b');");

        var result = await session.ExecuteSqlAsync("SELECT * FROM t ORDER BY id;");

        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data!.Rows.Count);
        Assert.Equal(1L, result.Data.Rows[0]["id"]);
        Assert.Equal("a", result.Data.Rows[0]["name"]);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public async Task ExecuteSqlAsync_MultiStatement_ReturnsLastResultSetAndTotalRowsAffected()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");

        var result = await session.ExecuteSqlAsync(
            "INSERT INTO t VALUES (1); INSERT INTO t VALUES (2); INSERT INTO t VALUES (3); SELECT COUNT(*) AS c FROM t;");

        Assert.NotNull(result.Data);
        Assert.Single(result.Data!.Rows);
        Assert.Equal(3L, result.Data.Rows[0]["c"]);
        Assert.Equal(3, result.RowsAffected);
    }

    [Fact]
    public async Task ExecuteSqlAsync_NonQuery_ReturnsNullDataAndRowsAffected()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        var result = await session.ExecuteSqlAsync("INSERT INTO t VALUES (1), (2);");

        Assert.Null(result.Data);
        Assert.Equal(2, result.RowsAffected);
    }

    [Fact]
    public async Task ExecuteSqlAsync_PreservesBlobAsByteArray()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER, data BLOB);");
        byte[] payload = [0x00, 0x01, 0xFF, 0x10, 0xAB];

        // Insert the blob via the paged-editing path so we exercise real parameter binding
        // (the ADO.NET provider binds byte[] as a BLOB parameter) rather than a SQL literal.
        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var row = page.Data.NewRow();
        row["id"] = 1;
        row["data"] = payload;
        page.Data.Rows.Add(row);
        await session.ApplyChangesAsync(page);

        var result = await session.ExecuteSqlAsync("SELECT data FROM t WHERE id = 1;");
        var blob = Assert.IsType<byte[]>(result.Data!.Rows[0]["data"]);
        Assert.Equal(payload, blob);
    }

    [Fact]
    public async Task ExecuteSqlAsync_InvalidSql_ThrowsSqlExecutionExceptionWithStatement()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() => session.ExecuteSqlAsync("SELECT * FROM no_such_table;"));
        Assert.Contains("no_such_table", ex.Message);
        Assert.Contains("no_such_table", ex.CommandText);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task ExecuteSqlAsync_LogsCommandExecutedEvents()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        var events = new List<Models.SqlCommandLogEntry>();
        session.CommandExecuted += (_, e) => events.Add(e.Entry);

        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        await Assert.ThrowsAsync<SqlExecutionException>(() => session.ExecuteSqlAsync("SELECT * FROM missing;"));

        Assert.Equal(2, events.Count);
        Assert.True(events[0].Success);
        Assert.False(events[1].Success);
        Assert.NotNull(events[1].Error);
    }

    [Fact]
    public async Task ExecuteSqlAsync_LargeQuery_HonorsCancellation()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(15));

        const string sql = "WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 50000000) " +
                            "SELECT x FROM cnt;";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteSqlAsync(sql, cts.Token));
    }

    [Fact]
    public async Task ExecuteSqlAsync_AlreadyCancelledToken_ThrowsImmediately()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteSqlAsync("SELECT 1;", cts.Token));
    }
}
