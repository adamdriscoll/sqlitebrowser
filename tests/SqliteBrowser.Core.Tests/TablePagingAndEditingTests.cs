using SqliteBrowser.Core.Models;
using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class TablePagingAndEditingTests
{
    [Fact]
    public async Task GetTablePageAsync_RowIdTable_ExposesHiddenRowIdColumnAndIsEditable()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (name TEXT); INSERT INTO t VALUES ('a'), ('b'), ('c');");

        var page = await session.GetTablePageAsync("main", "t", 0, 10);

        Assert.Equal(RowIdentityKind.RowId, page.Identity);
        Assert.False(page.IsReadOnly);
        Assert.Equal(3, page.TotalRows);
        Assert.Equal(3, page.Data.Rows.Count);
        Assert.True(page.Data.Columns.Contains(TablePage.RowIdColumnName));
        Assert.Equal(new[] { TablePage.RowIdColumnName }, page.KeyColumns);
    }

    [Fact]
    public async Task GetTablePageAsync_Pagination_RespectsOffsetAndPageSize()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER); " +
            string.Join(" ", Enumerable.Range(1, 25).Select(i => $"INSERT INTO t VALUES ({i});")));

        var page = await session.GetTablePageAsync("main", "t", 10, 5, orderBy: "n ASC");

        Assert.Equal(25, page.TotalRows);
        Assert.Equal(5, page.Data.Rows.Count);
        Assert.Equal(11L, page.Data.Rows[0]["n"]);
        Assert.Equal(15L, page.Data.Rows[4]["n"]);
    }

    [Fact]
    public async Task GetTablePageAsync_WithFilter_RestrictsRows()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER); INSERT INTO t VALUES (1),(2),(3),(4),(5);");

        var page = await session.GetTablePageAsync("main", "t", 0, 10, filter: "n > 2");

        Assert.Equal(3, page.TotalRows);
        Assert.Equal(3, page.Data.Rows.Count);
    }

    [Fact]
    public async Task GetTablePageAsync_View_IsReadOnly()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            "CREATE TABLE t (n INTEGER); INSERT INTO t VALUES (1),(2); CREATE VIEW v AS SELECT n FROM t;");

        var page = await session.GetTablePageAsync("main", "v", 0, 10);

        Assert.Equal(RowIdentityKind.None, page.Identity);
        Assert.True(page.IsReadOnly);
        Assert.Empty(page.KeyColumns);
    }

    [Fact]
    public async Task GetTablePageAsync_WithoutRowId_UsesPrimaryKeyIdentity()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync(
            "CREATE TABLE t (k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID; INSERT INTO t VALUES ('a','1'), ('b','2');");

        var page = await session.GetTablePageAsync("main", "t", 0, 10);

        Assert.Equal(RowIdentityKind.PrimaryKey, page.Identity);
        Assert.False(page.IsReadOnly);
        Assert.Equal(new[] { "k" }, page.KeyColumns);
        Assert.False(page.Data.Columns.Contains(TablePage.RowIdColumnName));
    }

    [Fact]
    public async Task GetTablePageAsync_UnknownTable_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetTablePageAsync("main", "does_not_exist", 0, 10));
    }

    [Fact]
    public async Task ApplyChangesAsync_Insert_PersistsAndAssignsRowId()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (name TEXT);");

        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var row = page.Data.NewRow();
        row["name"] = "new-row";
        page.Data.Rows.Add(row);

        int affected = await session.ApplyChangesAsync(page);

        Assert.Equal(1, affected);
        Assert.True((long)row[TablePage.RowIdColumnName] > 0);

        var check = await session.ExecuteSqlAsync("SELECT name FROM t;");
        Assert.Equal("new-row", check.Data!.Rows[0]["name"]);
    }

    [Fact]
    public async Task ApplyChangesAsync_Update_PersistsChangedValue()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (name TEXT); INSERT INTO t VALUES ('old');");

        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        page.Data.Rows[0]["name"] = "updated";

        int affected = await session.ApplyChangesAsync(page);

        Assert.Equal(1, affected);
        var check = await session.ExecuteSqlAsync("SELECT name FROM t;");
        Assert.Equal("updated", check.Data!.Rows[0]["name"]);
    }

    [Fact]
    public async Task ApplyChangesAsync_Delete_RemovesRow()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (name TEXT); INSERT INTO t VALUES ('a'), ('b');");

        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        page.Data.Rows[0].Delete();

        int affected = await session.ApplyChangesAsync(page);

        Assert.Equal(1, affected);
        var check = await session.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(1L, check.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task ApplyChangesAsync_InsertUpdateDeleteTogether_AllPersist()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (name TEXT); INSERT INTO t VALUES ('keep'), ('remove'), ('edit');");

        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var toDelete = page.Data.Rows.Cast<DataRow>().Single(r => (string)r["name"] == "remove");
        var toEdit = page.Data.Rows.Cast<DataRow>().Single(r => (string)r["name"] == "edit");
        toDelete.Delete();
        toEdit["name"] = "edited";
        var newRow = page.Data.NewRow();
        newRow["name"] = "inserted";
        page.Data.Rows.Add(newRow);

        int affected = await session.ApplyChangesAsync(page);
        Assert.Equal(3, affected);

        var names = (await session.ExecuteSqlAsync("SELECT name FROM t ORDER BY name;"))
            .Data!.Rows.Cast<DataRow>().Select(r => (string)r["name"]).ToArray();
        Assert.Equal(new[] { "edited", "inserted", "keep" }, names);
    }

    [Fact]
    public async Task ApplyChangesAsync_ReadOnlyPage_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER); INSERT INTO t VALUES (1); CREATE VIEW v AS SELECT n FROM t;");

        var page = await session.GetTablePageAsync("main", "v", 0, 10);
        var row = page.Data.NewRow();
        row["n"] = 2;
        page.Data.Rows.Add(row);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyChangesAsync(page));
    }

    [Fact]
    public async Task ApplyChangesAsync_ParticipatesInEditTransaction_WhenActive()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (name TEXT);");

        await session.BeginEditAsync();
        var page = await session.GetTablePageAsync("main", "t", 0, 10);
        var row = page.Data.NewRow();
        row["name"] = "pending";
        page.Data.Rows.Add(row);
        await session.ApplyChangesAsync(page);

        Assert.True(session.IsDirty);
        await session.RevertEditAsync();

        var check = await session.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(0L, check.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task GetTablePageAsync_InvalidOffsetOrPageSize_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER);");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.GetTablePageAsync("main", "t", -1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.GetTablePageAsync("main", "t", 0, 0));
    }
}
