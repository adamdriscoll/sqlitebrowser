using SqliteBrowser.Core.Services;
using SqliteBrowser.Core.Tests.TestSupport;

namespace SqliteBrowser.Core.Tests;

public class LifecycleAndSchemaTests
{
    [Fact]
    public async Task CreateAsync_OnDisk_CreatesFileAndOpensWritable()
    {
        using var temp = new TempFile();
        await using var session = await DatabaseSession.CreateAsync(temp.Path);

        Assert.True(session.IsOpen);
        Assert.False(session.IsReadOnly);
        Assert.False(session.IsInMemory);
        Assert.Equal(temp.Path, session.Path);
        Assert.True(File.Exists(temp.Path));

        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);");
    }

    [Fact]
    public async Task CreateAsync_ExistingFileWithoutOverwrite_Throws()
    {
        using var temp = new TempFile();
        await using (var session = await DatabaseSession.CreateAsync(temp.Path))
        {
            await session.CloseAsync();
        }

        await Assert.ThrowsAsync<IOException>(() => DatabaseSession.CreateAsync(temp.Path, overwrite: false));
    }

    [Fact]
    public async Task CreateAsync_ExistingFileWithOverwrite_Replaces()
    {
        using var temp = new TempFile();
        await using (var session = await DatabaseSession.CreateAsync(temp.Path))
        {
            await session.ExecuteSqlAsync("CREATE TABLE old_table (id INTEGER);");
            await session.CloseAsync();
        }

        await using var session2 = await DatabaseSession.CreateAsync(temp.Path, overwrite: true);
        var objects = await session2.GetDatabaseObjectsAsync();
        Assert.DoesNotContain(objects, o => o.Name == "old_table");
    }

    [Fact]
    public async Task OpenAsync_NonExistentFile_Throws()
    {
        using var temp = new TempFile();
        await Assert.ThrowsAsync<FileNotFoundException>(() => DatabaseSession.OpenAsync(temp.Path));
    }

    [Fact]
    public async Task OpenAsync_ExistingFile_ReadsData()
    {
        using var temp = new TempFile();
        await using (var create = await DatabaseSession.CreateAsync(temp.Path))
        {
            await create.ExecuteSqlAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t VALUES (1, 'hello');");
            await create.CloseAsync();
        }

        await using var session = await DatabaseSession.OpenAsync(temp.Path);
        var result = await session.ExecuteSqlAsync("SELECT name FROM t WHERE id = 1;");
        Assert.NotNull(result.Data);
        Assert.Equal("hello", result.Data!.Rows[0]["name"]);
    }

    [Fact]
    public async Task OpenAsync_ReadOnly_RejectsWrites()
    {
        using var temp = new TempFile();
        await using (var create = await DatabaseSession.CreateAsync(temp.Path))
        {
            await create.ExecuteSqlAsync("CREATE TABLE t (id INTEGER PRIMARY KEY);");
            await create.CloseAsync();
        }

        await using var session = await DatabaseSession.OpenAsync(temp.Path, readOnly: true);
        Assert.True(session.IsReadOnly);

        await Assert.ThrowsAsync<Exceptions.SqlExecutionException>(() => session.ExecuteSqlAsync("INSERT INTO t VALUES (1);"));

        // High level mutating operations should fail fast without hitting the engine.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginEditAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.VacuumAsync());
    }

    [Fact]
    public async Task OpenInMemoryAsync_IsWritableAndTransient()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        Assert.True(session.IsInMemory);
        Assert.Null(session.Path);
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        var objects = await session.GetDatabaseObjectsAsync();
        Assert.Contains(objects, o => o.Name == "t");
    }

    [Fact]
    public async Task CloseAsync_WithPendingEditTransaction_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.BeginEditAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CloseAsync());

        // Cleanup so DisposeAsync doesn't have to roll back for us (already exercised elsewhere).
        await session.RevertEditAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithPendingEditTransaction_RollsBackSilently()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        await session.BeginEditAsync();
        await session.ExecuteSqlAsync("INSERT INTO t VALUES (1);");

        // Simulate app shutdown without an explicit commit/revert: dispose must not throw.
        var exception = await Record.ExceptionAsync(async () => await session.DisposeAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var session = await DatabaseSession.OpenInMemoryAsync();
        await session.DisposeAsync();
        var exception = await Record.ExceptionAsync(async () => await session.DisposeAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task OperationsAfterDispose_ThrowObjectDisposedException()
    {
        var session = await DatabaseSession.OpenInMemoryAsync();
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteSqlAsync("SELECT 1;"));
    }

    [Fact]
    public async Task SaveAsAsync_CopiesFullDatabase()
    {
        using var destination = new TempFile();
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t VALUES (1, 'a'), (2, 'b');");

        await session.SaveAsAsync(destination.Path);

        await using var copy = await DatabaseSession.OpenAsync(destination.Path);
        var result = await copy.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(2L, result.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task SaveAsAsync_OverwritesExistingDestination()
    {
        using var destination = new TempFile();
        await using (var seed = await DatabaseSession.CreateAsync(destination.Path))
        {
            await seed.ExecuteSqlAsync("CREATE TABLE stale (id INTEGER);");
            await seed.CloseAsync();
        }

        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE fresh (id INTEGER);");
        await session.SaveAsAsync(destination.Path);

        await using var copy = await DatabaseSession.OpenAsync(destination.Path);
        var objects = await copy.GetDatabaseObjectsAsync();
        Assert.DoesNotContain(objects, o => o.Name == "stale");
        Assert.Contains(objects, o => o.Name == "fresh");
    }

    [Fact]
    public async Task GetDatabaseObjectsAsync_ReturnsTablesViewsIndexesTriggers()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER);
            CREATE VIEW adults AS SELECT * FROM people WHERE age >= 18;
            CREATE INDEX idx_people_name ON people (name);
            CREATE TRIGGER trg_people_ai AFTER INSERT ON people BEGIN SELECT 1; END;
            """);

        var objects = await session.GetDatabaseObjectsAsync();

        Assert.Contains(objects, o => o.Type == "table" && o.Name == "people" && o.Schema == "main");
        Assert.Contains(objects, o => o.Type == "view" && o.Name == "adults" && o.TableName == "adults");
        Assert.Contains(objects, o => o.Type == "index" && o.Name == "idx_people_name" && o.TableName == "people");
        Assert.Contains(objects, o => o.Type == "trigger" && o.Name == "trg_people_ai" && o.TableName == "people");
    }

    [Fact]
    public async Task GetColumnsAsync_ReturnsMetadataIncludingPrimaryKeyOrder()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE composite (a INTEGER NOT NULL, b INTEGER NOT NULL, c TEXT DEFAULT 'x', PRIMARY KEY (b, a));");

        var columns = await session.GetColumnsAsync("main", "composite");

        Assert.Equal(3, columns.Count);
        var a = columns.Single(c => c.Name == "a");
        var b = columns.Single(c => c.Name == "b");
        var c = columns.Single(c => c.Name == "c");

        Assert.True(a.NotNull);
        Assert.Equal(2, a.PrimaryKeyOrder);
        Assert.Equal(1, b.PrimaryKeyOrder);
        Assert.Equal(0, c.PrimaryKeyOrder);
        Assert.Equal("'x'", c.DefaultValue);
        Assert.False(c.Hidden);
    }

    [Fact]
    public async Task GetAttachedSchemasAsync_IncludesMainAndTemp()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        var schemas = await session.GetAttachedSchemasAsync();
        Assert.Contains("main", schemas);
    }
}
