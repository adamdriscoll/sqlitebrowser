using SqliteBrowser.Core.Exceptions;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class PragmaAndMaintenanceTests
{
    [Fact]
    public async Task GetPragmaAsync_ScalarPragma_ReturnsScalarValue()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        var pragma = await session.GetPragmaAsync("foreign_keys");

        Assert.Equal("foreign_keys", pragma.Name);
        Assert.NotNull(pragma.ScalarValue);
        Assert.Equal(1L, pragma.ScalarValue); // set ON by ApplyDefaultPragmasAsync
    }

    [Fact]
    public async Task SetPragmaAsync_ChangesValue()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.SetPragmaAsync("foreign_keys", "OFF");

        var pragma = await session.GetPragmaAsync("foreign_keys");
        Assert.Equal(0L, pragma.ScalarValue);
    }

    [Fact]
    public async Task SetPragmaAsync_RejectsSemicolonInValue()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => session.SetPragmaAsync("foreign_keys", "OFF; DROP TABLE x"));
    }

    [Fact]
    public async Task GetPragmaAsync_MultiRowPragma_ReturnsMultipleRows()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER, name TEXT);");

        var pragma = await session.GetPragmaAsync("table_info", tableArgument: "t");
        Assert.Equal(2, pragma.Rows.Count);
        Assert.Null(pragma.ScalarValue);
    }

    [Fact]
    public async Task AttachAndDetach_AllowsCrossDatabaseQueries()
    {
        using var attachedFile = new TestSupport.TempFile();
        await using (var seed = await DatabaseSession.CreateAsync(attachedFile.Path))
        {
            await seed.ExecuteSqlAsync("CREATE TABLE other (id INTEGER); INSERT INTO other VALUES (42);");
            await seed.CloseAsync();
        }

        // Ahtola requires the primary connection to be file-backed for ATTACH (see remarks on
        // DatabaseSession.AttachAsync), so this uses a disk-backed primary rather than :memory:.
        using var primaryFile = new TestSupport.TempFile();
        await using var session = await DatabaseSession.CreateAsync(primaryFile.Path);
        await session.AttachAsync(attachedFile.Path, "ext");

        var schemas = await session.GetAttachedSchemasAsync();
        Assert.Contains("ext", schemas);

        var result = await session.ExecuteSqlAsync("SELECT id FROM ext.other;");
        Assert.Equal(42L, result.Data!.Rows[0]["id"]);

        await session.DetachAsync("ext");
        schemas = await session.GetAttachedSchemasAsync();
        Assert.DoesNotContain("ext", schemas);
    }

    [Fact]
    public async Task AttachAsync_FromInMemoryPrimary_ThrowsDocumentedAhtolaLimitation()
    {
        // Ahtola limitation: unlike native SQLite, the managed engine cannot ATTACH a file onto an
        // :memory:-opened primary connection. This is intentionally surfaced, not silently ignored.
        using var attachedFile = new TestSupport.TempFile();
        await using var session = await DatabaseSession.OpenInMemoryAsync();

        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() => session.AttachAsync(attachedFile.Path, "ext"));
        Assert.Contains("file-backed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VacuumAsync_Plain_Succeeds()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);");
        var exception = await Record.ExceptionAsync(() => session.VacuumAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task VacuumAsync_Into_CreatesCompactedCopy()
    {
        // Ahtola requires the source connection to be file-backed for VACUUM INTO (see remarks on
        // DatabaseSession.VacuumAsync), so this uses a disk-backed session rather than :memory:.
        using var source = new TestSupport.TempFile();
        using var destination = new TestSupport.TempFile();
        await using var session = await DatabaseSession.CreateAsync(source.Path);
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1), (2), (3);");

        await session.VacuumAsync(destination.Path);

        Assert.True(File.Exists(destination.Path));
        await using var copy = await DatabaseSession.OpenAsync(destination.Path);
        var count = await copy.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(3L, count.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task VacuumAsync_Into_FromInMemorySession_ThrowsDocumentedAhtolaLimitation()
    {
        // Ahtola limitation: unlike native SQLite, VACUUM INTO cannot run against an
        // :memory:-opened session under the managed engine. This is intentionally surfaced, not
        // silently ignored.
        using var destination = new TestSupport.TempFile();
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);");

        var ex = await Assert.ThrowsAsync<SqlExecutionException>(() => session.VacuumAsync(destination.Path));
        Assert.Contains("file-backed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IntegrityCheckAsync_HealthyDatabase_ReturnsOk()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        var messages = await session.IntegrityCheckAsync();
        Assert.Contains("ok", messages);
    }

    [Fact]
    public async Task QuickCheckAsync_HealthyDatabase_ReturnsOk()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        var messages = await session.QuickCheckAsync();
        Assert.Contains("ok", messages);
    }

    [Fact]
    public async Task ForeignKeyCheckAsync_NoViolations_ReturnsEmpty()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            "CREATE TABLE parent (id INTEGER PRIMARY KEY); " +
            "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id)); " +
            "INSERT INTO parent VALUES (1); INSERT INTO child VALUES (1, 1);");

        var violations = await session.ForeignKeyCheckAsync();
        Assert.Empty(violations);
    }

    [Fact]
    public async Task ForeignKeyCheckAsync_DetectsViolation()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        // Disable enforcement so the invalid insert succeeds, then check for the resulting violation.
        await session.SetPragmaAsync("foreign_keys", "OFF");
        await session.ExecuteSqlAsync(
            "CREATE TABLE parent (id INTEGER PRIMARY KEY); " +
            "CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id)); " +
            "INSERT INTO child VALUES (1, 999);");

        var violations = await session.ForeignKeyCheckAsync();
        Assert.Single(violations);
        Assert.Equal("child", violations[0].Table);
        Assert.Equal("parent", violations[0].Parent);
    }

    [Fact]
    public async Task OptimizeAsync_Succeeds()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (id INTEGER);");
        var exception = await Record.ExceptionAsync(() => session.OptimizeAsync());
        Assert.Null(exception);
    }
}
