using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class TransactionTests
{
    [Fact]
    public async Task BeginEditAsync_TwiceThrows()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.BeginEditAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginEditAsync());
        await session.RevertEditAsync();
    }

    [Fact]
    public async Task CommitEditAsync_WithoutBegin_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitEditAsync());
    }

    [Fact]
    public async Task RevertEditAsync_WithoutBegin_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RevertEditAsync());
    }

    [Fact]
    public async Task CommitEditAsync_PersistsChanges()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");

        await session.BeginEditAsync();
        Assert.True(session.HasPendingEdit);
        Assert.False(session.IsDirty);

        await session.ExecuteSqlAsync("INSERT INTO t VALUES (1);");
        Assert.True(session.IsDirty);

        await session.CommitEditAsync();
        Assert.False(session.HasPendingEdit);
        Assert.False(session.IsDirty);

        var result = await session.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(1L, result.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task RevertEditAsync_DiscardsChanges()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");

        await session.BeginEditAsync();
        await session.ExecuteSqlAsync("INSERT INTO t VALUES (1);");
        Assert.True(session.IsDirty);

        await session.RevertEditAsync();
        Assert.False(session.HasPendingEdit);
        Assert.False(session.IsDirty);

        var result = await session.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(0L, result.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task AfterCommit_NewCommandsDoNotAttachToStaleTransaction()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");

        await session.BeginEditAsync();
        await session.ExecuteSqlAsync("INSERT INTO t VALUES (1);");
        await session.CommitEditAsync();

        // This statement must run in a fresh autocommit context, not against the disposed transaction.
        var result = await session.ExecuteSqlAsync("INSERT INTO t VALUES (2); SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(2L, result.Data!.Rows[0]["c"]);
        Assert.False(session.HasPendingEdit);
    }

    [Fact]
    public async Task AfterRevert_NewCommandsDoNotAttachToStaleTransaction()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");

        await session.BeginEditAsync();
        await session.ExecuteSqlAsync("INSERT INTO t VALUES (1);");
        await session.RevertEditAsync();

        var result = await session.ExecuteSqlAsync("INSERT INTO t VALUES (2); SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(1L, result.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task BeginEditAsync_OnReadOnlySession_Throws()
    {
        await using var readOnly = await DatabaseSession.OpenInMemoryAsync();
        // OpenInMemoryAsync always creates writable memory DBs; simulate read-only via a file instead.
        using var temp = new TestSupport.TempFile();
        await using (var create = await DatabaseSession.CreateAsync(temp.Path))
        {
            await create.CloseAsync();
        }

        await using var session = await DatabaseSession.OpenAsync(temp.Path, readOnly: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginEditAsync());
    }

    [Fact]
    public async Task ExecuteSqlAsync_OutsideEditTransaction_AutoCommitsEachStatement()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        Assert.False(session.IsDirty);
        await session.ExecuteSqlAsync("INSERT INTO t VALUES (1);");

        // No edit transaction was ever started, so dirty tracking never engages.
        Assert.False(session.IsDirty);
        Assert.False(session.HasPendingEdit);
    }
}
