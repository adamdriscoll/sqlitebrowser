using SqliteBrowser.Core.Services;

namespace SqliteBrowser.Core.Tests;

public class IdentifierAndFilterValidationTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("has space")]
    [InlineData("has\"quote")]
    [InlineData("select")] // reserved word
    [InlineData("has'apostrophe")]
    public void Quote_RoundTripsExoticNames(string name)
    {
        string quoted = SqlIdentifier.Quote(name);
        Assert.StartsWith("\"", quoted);
        Assert.EndsWith("\"", quoted);
        // Any embedded double quote must be doubled.
        if (name.Contains('"'))
        {
            Assert.Contains("\"\"", quoted);
        }
    }

    [Fact]
    public void Quote_NullCharacter_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote("bad\0name"));
    }

    [Fact]
    public void Quote_EmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote(string.Empty));
    }

    [Fact]
    public void QuoteQualified_ProducesSchemaDotName()
    {
        string result = SqlIdentifier.QuoteQualified("main", "my table");
        Assert.Equal("\"main\".\"my table\"", result);
    }

    [Theory]
    [InlineData("id = 1; DROP TABLE t")]
    [InlineData("id = 1 -- comment")]
    [InlineData("id = 1 /* block */")]
    [InlineData("id = 1\0")]
    public void ValidateFilterExpression_RejectsInjectionAttempts(string filter)
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifier.ValidateFilterExpression(filter));
    }

    [Theory]
    [InlineData("id = 1")]
    [InlineData("name LIKE 'a%' AND age > 18")]
    [InlineData("id IN (1,2,3)")]
    public void ValidateFilterExpression_AllowsOrdinaryBooleanExpressions(string filter)
    {
        var exception = Record.Exception(() => SqlIdentifier.ValidateFilterExpression(filter));
        Assert.Null(exception);
    }

    [Fact]
    public async Task GetTablePageAsync_FilterWithSemicolon_IsRejected()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER); INSERT INTO t VALUES (1);");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.GetTablePageAsync("main", "t", 0, 10, filter: "1=1; DROP TABLE t"));

        // Table must still exist since the injection attempt was rejected before reaching the engine.
        var check = await session.ExecuteSqlAsync("SELECT COUNT(*) AS c FROM t;");
        Assert.Equal(1L, check.Data!.Rows[0]["c"]);
    }

    [Fact]
    public async Task GetTablePageAsync_OrderByWithComment_IsRejected()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await session.ExecuteSqlAsync("CREATE TABLE t (n INTEGER);");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.GetTablePageAsync("main", "t", 0, 10, orderBy: "n -- inject"));
    }

    [Fact]
    public async Task GetColumnsAsync_SchemaWithNulCharacter_Throws()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => session.GetColumnsAsync("ma\0in", "t"));
    }

    [Fact]
    public async Task ExecuteSqlAsync_CanCreateTableWithExoticQuotedName()
    {
        await using var session = await DatabaseSession.OpenInMemoryAsync();
        string quotedName = SqlIdentifier.Quote("weird name \"with\" quotes");
        await session.ExecuteSqlAsync($"CREATE TABLE {quotedName} (id INTEGER);");

        var objects = await session.GetDatabaseObjectsAsync();
        Assert.Contains(objects, o => o.Name == "weird name \"with\" quotes");
    }
}
