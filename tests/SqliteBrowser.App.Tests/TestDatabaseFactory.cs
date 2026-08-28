using SqliteBrowser.Core.Services;

namespace SqliteBrowser.App.Tests;

/// <summary>
/// Creates small temporary SQLite database files (via the real <see cref="DatabaseSession"/>, not
/// hand-rolled ADO.NET) for tests to open through the application. Each call returns a unique path
/// under the OS temp directory; callers are responsible for deleting it (see <see cref="Delete"/>).
/// </summary>
internal static class TestDatabaseFactory
{
    /// <summary>Creates a temp file path that does not yet exist.</summary>
    public static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"sqlitebrowser-apptest-{Guid.NewGuid():N}.db");

    /// <summary>Creates a small database with one table and a couple of rows, ready to be opened by the app.</summary>
    public static async Task<string> CreateSampleDatabaseAsync()
    {
        string path = NewTempPath();
        await using (var session = await DatabaseSession.CreateAsync(path).ConfigureAwait(false))
        {
            await session.BeginEditAsync().ConfigureAwait(false);
            await session.ExecuteSqlAsync(
                "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER);" +
                "INSERT INTO people (name, age) VALUES ('Ada', 30);" +
                "INSERT INTO people (name, age) VALUES ('Grace', 45);" +
                "INSERT INTO people (name, age) VALUES ('Linus', 52);").ConfigureAwait(false);
            await session.CommitEditAsync().ConfigureAwait(false);
            await session.CloseAsync().ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Creates a small database with one table that has a declared <c>BLOB</c> column, seeded with a real binary value.</summary>
    public static async Task<string> CreateBlobSampleDatabaseAsync()
    {
        string path = NewTempPath();
        await using (var session = await DatabaseSession.CreateAsync(path).ConfigureAwait(false))
        {
            await session.BeginEditAsync().ConfigureAwait(false);
            // X'...' is SQLite's blob literal syntax - no parameter binding needed to seed real binary data.
            await session.ExecuteSqlAsync(
                "CREATE TABLE assets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, payload BLOB);" +
                "INSERT INTO assets (name, payload) VALUES ('logo', X'0102030405FF');").ConfigureAwait(false);
            await session.CommitEditAsync().ConfigureAwait(false);
            await session.CloseAsync().ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Best-effort delete; ignores failures so cleanup never masks a test's real assertion failure.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
