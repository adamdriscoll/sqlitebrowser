using SqliteBrowser.Core.Exceptions;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class SqlDumpTests
{
    [Fact]
    public async Task ExportSqlDumpAsync_ContainsSchemaAndInserts()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t VALUES (1, 'Alice'), (2, 'Bob');");

        var writer = new StringWriter();
        await session.ExportSqlDumpAsync(writer);
        string dump = writer.ToString();

        Assert.Contains("BEGIN TRANSACTION;", dump);
        Assert.Contains("COMMIT;", dump);
        Assert.Contains("CREATE TABLE", dump);
        Assert.Contains("INSERT INTO", dump);
        Assert.Contains("'Alice'", dump);
    }
    [Fact]
    public async Task ExportSqlDumpAsync_EncodesBlobAsHexLiteral()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (data BLOB);");
        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var row = page.Data.NewRow();
        row["data"] = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        page.Data.Rows.Add(row);
        await session.ApplyChangesAsync(page);

        var writer = new StringWriter();
        await session.ExportSqlDumpAsync(writer);

        Assert.Contains("X'DEADBEEF'", writer.ToString());
    }

    [Fact]
    public async Task ExportSqlDumpAsync_ThenImportSqlScriptAsync_RoundTripsIntoFreshDatabase()
    {
        await using var source = await DatabaseSession.OpenInMemoryAsync();
        await source.ExecuteSqlAsync(
            """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER);
            INSERT INTO people VALUES (1, 'Alice', 30), (2, 'Bob', 25);
            CREATE VIEW adults AS SELECT * FROM people WHERE age >= 18;
            """);

        var writer = new StringWriter();
        await source.ExportSqlDumpAsync(writer);
        string dump = writer.ToString();

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        await target.ImportSqlScriptAsync(dump);

        var people = await target.ExecuteSqlAsync("SELECT id, name, age FROM people ORDER BY id;");
        Assert.Equal(2, people.Data!.Rows.Count);
        Assert.Equal("Alice", people.Data.Rows[0]["name"]);
        Assert.Equal(30L, people.Data.Rows[0]["age"]);

        var objects = await target.GetDatabaseObjectsAsync();
        Assert.Contains(objects, o => o.Type == "view" && o.Name == "adults");
    }

    [Fact]
    public async Task ExportSqlDumpFileAsync_And_ImportSqlScriptFileAsync_RoundTripThroughDisk()
    {
        await using var source = await DatabaseSession.OpenInMemoryAsync();
        await source.ExecuteSqlAsync("CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1), (2), (3);");

        using var dumpFile = new TestSupport.TempFile(".sql");
        await source.ExportSqlDumpFileAsync(dumpFile.Path);

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        var result = await target.ImportSqlScriptFileAsync(dumpFile.Path);
        Assert.NotNull(result);

        var count = await target.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(3L, count.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task ImportSqlScriptAsync_ExportedDump_UnderActiveEditTransaction_DoesNotThrow_AndLeavesAmbientTransactionActive()
    {
        // Regression test: ExportSqlDumpAsync wraps its output in "BEGIN TRANSACTION; ... COMMIT;".
        // Importing that dump while the target already has its own long-lived edit transaction
        // active must not attempt a nested BEGIN, and must not commit the caller's ambient,
        // unrelated pending edits by literally executing the dump's own "COMMIT;".
        await using var source = await DatabaseSession.OpenInMemoryAsync();
        await source.ExecuteSqlAsync("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO people VALUES (1, 'Alice');");
        var writer = new StringWriter();
        await source.ExportSqlDumpAsync(writer);
        string dump = writer.ToString();
        Assert.Contains("BEGIN TRANSACTION;", dump);
        Assert.Contains("COMMIT;", dump);

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        await target.ExecuteSqlAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT);");
        await target.BeginEditAsync();
        await target.ExecuteSqlAsync("INSERT INTO notes VALUES (1, 'unrelated pending edit');");
        Assert.True(target.IsDirty);

        var result = await target.ImportSqlScriptAsync(dump);

        Assert.NotNull(result);
        Assert.True(target.HasPendingEdit, "The ambient edit transaction must still be active after import.");
        Assert.True(target.IsDirty);

        // Imported rows are visible within the same (still uncommitted) transaction.
        var people = await target.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM people;");
        Assert.Equal(1L, people.Data!.Rows[0]["c"]);

        await target.CommitEditAsync();
    }

    [Fact]
    public async Task ImportSqlScriptAsync_ExportedDump_UnderActiveEditTransaction_RevertDiscardsImportAndPriorPendingEdit()
    {
        await using var source = await DatabaseSession.OpenInMemoryAsync();
        await source.ExecuteSqlAsync("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO people VALUES (1, 'Alice');");
        var writer = new StringWriter();
        await source.ExportSqlDumpAsync(writer);
        string dump = writer.ToString();

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        await target.ExecuteSqlAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT);");
        await target.BeginEditAsync();
        await target.ExecuteSqlAsync("INSERT INTO notes VALUES (1, 'unrelated pending edit');");

        await target.ImportSqlScriptAsync(dump);
        await target.RevertEditAsync();

        Assert.False(target.HasPendingEdit);
        Assert.False(target.IsDirty);

        // Both the pre-import pending edit and the imported data lived in the same ambient
        // transaction, so reverting must discard both together.
        var notes = await target.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM notes;");
        Assert.Equal(0L, notes.Data!.Rows[0]["c"]);

        var objects = await target.GetDatabaseObjectsAsync();
        Assert.DoesNotContain(objects, o => o.Name == "people");
    }

    [Fact]
    public async Task ImportSqlScriptAsync_ExportedDump_UnderActiveEditTransaction_CommitPersistsImportAndPriorPendingEdit()
    {
        await using var source = await DatabaseSession.OpenInMemoryAsync();
        await source.ExecuteSqlAsync("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO people VALUES (1, 'Alice');");
        var writer = new StringWriter();
        await source.ExportSqlDumpAsync(writer);
        string dump = writer.ToString();

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        await target.ExecuteSqlAsync("CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT);");
        await target.BeginEditAsync();
        await target.ExecuteSqlAsync("INSERT INTO notes VALUES (1, 'unrelated pending edit');");

        await target.ImportSqlScriptAsync(dump);
        await target.CommitEditAsync();

        Assert.False(target.HasPendingEdit);

        var notes = await target.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM notes;");
        Assert.Equal(1L, notes.Data!.Rows[0]["c"]);

        var people = await target.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM people;");
        Assert.Equal(1L, people.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task ImportSqlScriptAsync_ExportedDump_PreservesTriggerBodyUnderActiveEditTransaction()
    {
        // Regression test for the wrapper-stripping logic: a trigger's own "BEGIN ... END;" body
        // must never be confused with (or damaged by removing) the dump's outer transaction wrapper.
        await using var source = await DatabaseSession.OpenInMemoryAsync();
        await source.ExecuteSqlAsync(
            """
            CREATE TABLE t (id INTEGER PRIMARY KEY, val INTEGER);
            CREATE TABLE log (msg TEXT);
            CREATE TRIGGER trg AFTER INSERT ON t BEGIN INSERT INTO log(msg) VALUES ('inserted:' || NEW.id); END;
            """);

        var writer = new StringWriter();
        await source.ExportSqlDumpAsync(writer);
        string dump = writer.ToString();

        await using var target = await DatabaseSession.OpenInMemoryAsync();
        await target.BeginEditAsync();
        await target.ImportSqlScriptAsync(dump);

        var objects = await target.GetDatabaseObjectsAsync();
        Assert.Contains(objects, o => o.Type == "trigger" && o.Name == "trg");

        await target.ExecuteSqlAsync("INSERT INTO t (id, val) VALUES (1, 100);");
        var log = await target.ExecuteSqlAsync("SELECT msg FROM log;");
        Assert.Equal(1, log.Data!.Rows.Count);
        Assert.Equal("inserted:1", log.Data.Rows[0]["msg"]);

        await target.CommitEditAsync();
    }

    [Fact]
    public async Task ImportSqlScriptAsync_WithoutAmbientTransaction_IsAtomic_FailedStatementRollsBackEarlierStatements()
    {
        // Regression test: standalone import (no ambient edit transaction) must be all-or-nothing,
        // even when the script's own BEGIN/COMMIT wrapper has been normalized away.
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        string script =
            """
            CREATE TABLE t (id INTEGER PRIMARY KEY);
            INSERT INTO t VALUES (1);
            INSERT INTO t VALUES (1);
            """; // duplicate primary key -> the third statement fails

        await Assert.ThrowsAsync<SqlExecutionException>(() => session.ImportSqlScriptAsync(script));

        var objects = await session.GetDatabaseObjectsAsync();
        Assert.DoesNotContain(objects, o => o.Name == "t");
    }

    [Fact]
    public async Task ImportSqlScriptAsync_ExportedDump_WithoutAmbientTransaction_IsAtomic_FailedStatementRollsBackEarlierStatements()
    {
        // Same atomicity guarantee, but exercised via an actual exported-dump-shaped script (with
        // its own BEGIN TRANSACTION/COMMIT wrapper and a leading PRAGMA) rather than a hand-written one.
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        string dump =
            """
            PRAGMA foreign_keys=OFF;
            BEGIN TRANSACTION;
            CREATE TABLE t (id INTEGER PRIMARY KEY);
            INSERT INTO t VALUES (1);
            INSERT INTO t VALUES (1);
            COMMIT;
            """;

        await Assert.ThrowsAsync<SqlExecutionException>(() => session.ImportSqlScriptAsync(dump));

        var objects = await session.GetDatabaseObjectsAsync();
        Assert.DoesNotContain(objects, o => o.Name == "t");
    }
}
