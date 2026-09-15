using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ahtola.Data.Sqlite;
using SqliteBrowser.Core.Exceptions;
using SqliteBrowser.Core.Models;

namespace SqliteBrowser.Core.Services;

/// <summary>
/// Owns a single SQLite connection and exposes an idiomatic, cancellation-aware surface for
/// browsing, editing, querying and maintaining a database, suitable for driving a UI layer.
/// </summary>
/// <remarks>
/// A <see cref="DatabaseSession"/> is not thread-safe: like <see cref="SqliteConnection"/> itself,
/// a given instance should be used by one logical caller at a time.
/// </remarks>
public sealed class DatabaseSession : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _editTransaction;
    private bool _disposed;

    private DatabaseSession(SqliteConnection connection, string? path, bool isReadOnly, bool isInMemory)
    {
        _connection = connection;
        Path = path;
        IsReadOnly = isReadOnly;
        IsInMemory = isInMemory;
    }

    /// <summary>Raised after every SQL command executed against this session, success or failure.</summary>
    public event EventHandler<SqlCommandLogEventArgs>? CommandExecuted;

    /// <summary>The database file path, or <c>null</c> for in-memory databases.</summary>
    public string? Path { get; }

    /// <summary>Whether the underlying connection was opened read-only.</summary>
    public bool IsReadOnly { get; }

    /// <summary>Whether this session is backed by an in-memory (<c>:memory:</c>) database.</summary>
    public bool IsInMemory { get; }

    /// <summary>Whether the underlying connection is currently open.</summary>
    public bool IsOpen => !_disposed && _connection.State == ConnectionState.Open;

    /// <summary>Whether an explicit long-lived edit transaction (see <see cref="BeginEditAsync"/>) is active.</summary>
    public bool HasPendingEdit => _editTransaction is not null;

    /// <summary>
    /// Whether the active edit transaction (if any) has uncommitted work. Reset to <c>false</c> by
    /// <see cref="BeginEditAsync"/>, <see cref="CommitEditAsync"/> and <see cref="RevertEditAsync"/>.
    /// </summary>
    public bool IsDirty { get; private set; }

    // ------------------------------------------------------------------------------------------
    // Lifecycle: open / create / close / dispose
    // ------------------------------------------------------------------------------------------

    /// <summary>Opens an existing on-disk (or <c>:memory:</c>) database.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static async Task<DatabaseSession> OpenAsync(string path, bool readOnly = false, CancellationToken ct = default)
    {
        ValidatePath(path);
        bool isMemory = IsMemoryPath(path);

        if (!isMemory && !File.Exists(path))
        {
            throw new FileNotFoundException($"Database file not found: {path}", path);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = isMemory ? ":memory:" : path,
            // Disabling pooling ensures the native SQLite handle (and its OS-level file lock) is
            // released synchronously with CloseAsync/DisposeAsync, so a subsequent overwrite or
            // save-as to the same path never races a lingering pooled connection.
            Pooling = false,
        };

        if (!isMemory)
        {
            builder.Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite;
        }

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ApplyDefaultPragmasAsync(connection, ct).ConfigureAwait(false);
        return new DatabaseSession(connection, isMemory ? null : path, readOnly, isMemory);
    }

    /// <summary>Creates a brand-new on-disk (or <c>:memory:</c>) database, optionally overwriting an existing file.</summary>
    /// <exception cref="IOException">The file already exists and <paramref name="overwrite"/> is <c>false</c>.</exception>
    public static async Task<DatabaseSession> CreateAsync(string path, bool overwrite = false, CancellationToken ct = default)
    {
        ValidatePath(path);
        bool isMemory = IsMemoryPath(path);

        if (!isMemory && File.Exists(path))
        {
            if (!overwrite)
            {
                throw new IOException($"A file already exists at '{path}'. Pass overwrite: true to replace it.");
            }

            File.Delete(path);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = isMemory ? ":memory:" : path,
            Pooling = false,
        };

        if (!isMemory)
        {
            builder.Mode = SqliteOpenMode.ReadWriteCreate;
        }

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await ApplyDefaultPragmasAsync(connection, ct).ConfigureAwait(false);
        return new DatabaseSession(connection, isMemory ? null : path, false, isMemory);
    }

    /// <summary>Creates a new private in-memory database for the lifetime of this session.</summary>
    public static Task<DatabaseSession> OpenInMemoryAsync(CancellationToken ct = default) => CreateAsync(":memory:", overwrite: false, ct);

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path must not be null or empty.", nameof(path));
        }

        if (path.Contains('\0'))
        {
            throw new ArgumentException("Path must not contain a NUL character.", nameof(path));
        }
    }

    private static bool IsMemoryPath(string path) => string.Equals(path, ":memory:", StringComparison.Ordinal);

    private static async Task ApplyDefaultPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the underlying connection. Throws if an edit transaction is still active — callers
    /// must explicitly <see cref="CommitEditAsync"/> or <see cref="RevertEditAsync"/> first so that
    /// pending edits are never discarded implicitly.
    /// </summary>
    public async Task CloseAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        if (_editTransaction is not null)
        {
            throw new InvalidOperationException("Cannot close the session while an edit transaction is active. Commit or revert it first.");
        }

        await _connection.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the session. Unlike <see cref="CloseAsync"/>, this rolls back any pending edit
    /// transaction so that <c>Dispose</c> never throws, matching standard IDisposable semantics.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_editTransaction is not null)
        {
            var tx = _editTransaction;
            _editTransaction = null;
            try
            {
                await tx.RollbackAsync().ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Best-effort cleanup during Dispose: the connection may already be unusable
                // (e.g. broken by a prior fatal error). Disposal must not throw.
            }
            finally
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The database session is not open.");
        }
    }

    private void EnsureWritable()
    {
        EnsureOpen();
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The database was opened as read-only.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // Save-as
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Saves a full copy of the current database to <paramref name="destinationPath"/> using
    /// SQLite's online backup API (<see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>),
    /// which safely copies a live database page-by-page. Any existing file at the destination is
    /// replaced. Note: once started, the underlying single-shot backup call cannot be interrupted
    /// mid-copy; <paramref name="ct"/> is honored only before the copy begins.
    /// </summary>
    public async Task SaveAsAsync(string destinationPath, CancellationToken ct = default)
    {
        EnsureOpen();
        ValidatePath(destinationPath);
        if (IsMemoryPath(destinationPath))
        {
            throw new ArgumentException("Cannot save to ':memory:'.", nameof(destinationPath));
        }

        if (_editTransaction is not null)
        {
            throw new InvalidOperationException("Cannot save while an edit transaction is active. Commit or revert it first.");
        }

        ct.ThrowIfCancellationRequested();

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var destination = new SqliteConnection(builder.ConnectionString);
        await using (destination.ConfigureAwait(false))
        {
            await destination.OpenAsync(ct).ConfigureAwait(false);
            await Task.Run(() => _connection.BackupDatabase(destination), ct).ConfigureAwait(false);
            await destination.CloseAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Explicit edit transaction (commit / revert / dirty state)
    // ------------------------------------------------------------------------------------------

    /// <summary>Begins a long-lived edit transaction. All subsequent mutating operations participate in it until commit/revert.</summary>
    public async Task BeginEditAsync(CancellationToken ct = default)
    {
        EnsureWritable();
        if (_editTransaction is not null)
        {
            throw new InvalidOperationException("An edit transaction is already active.");
        }

        _editTransaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        IsDirty = false;
    }

    /// <summary>Commits the active edit transaction, persisting all changes made since <see cref="BeginEditAsync"/>.</summary>
    public async Task CommitEditAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        if (_editTransaction is null)
        {
            throw new InvalidOperationException("No edit transaction is active.");
        }

        var tx = _editTransaction;
        // Clear the field before awaiting so no command created concurrently (or by re-entrant
        // code) can ever attach to a transaction that is about to be committed/disposed.
        _editTransaction = null;
        try
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }

        IsDirty = false;
    }

    /// <summary>Rolls back the active edit transaction, discarding all changes made since <see cref="BeginEditAsync"/>.</summary>
    public async Task RevertEditAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        if (_editTransaction is null)
        {
            throw new InvalidOperationException("No edit transaction is active.");
        }

        var tx = _editTransaction;
        _editTransaction = null;
        try
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
        }

        IsDirty = false;
    }

    // ------------------------------------------------------------------------------------------
    // Internal command plumbing (logging + transaction attachment)
    // ------------------------------------------------------------------------------------------

    private SqliteCommand CreateCommand(string sql) => CreateCommand(sql, _editTransaction);

    private SqliteCommand CreateCommand(string sql, SqliteTransaction? transaction)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }

    private void RaiseCommandExecuted(string commandText, TimeSpan elapsed, bool success, string? error)
    {
        CommandExecuted?.Invoke(this, new SqlCommandLogEventArgs(new SqlCommandLogEntry(commandText, DateTimeOffset.UtcNow, elapsed, success, error)));
    }

    private async Task<SqliteDataReader> ExecuteReaderLoggedAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            sw.Stop();
            RaiseCommandExecuted(cmd.CommandText, sw.Elapsed, true, null);
            return (SqliteDataReader)reader;
        }
        catch (SqliteException ex)
        {
            sw.Stop();
            RaiseCommandExecuted(cmd.CommandText, sw.Elapsed, false, ex.Message);
            throw new SqlExecutionException(cmd.CommandText, ex);
        }
    }

    private async Task<int> ExecuteNonQueryLoggedAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            sw.Stop();
            RaiseCommandExecuted(cmd.CommandText, sw.Elapsed, true, null);
            if (_editTransaction is not null)
            {
                IsDirty = true;
            }

            return rows;
        }
        catch (SqliteException ex)
        {
            sw.Stop();
            RaiseCommandExecuted(cmd.CommandText, sw.Elapsed, false, ex.Message);
            throw new SqlExecutionException(cmd.CommandText, ex);
        }
    }

    private async Task<object?> ExecuteScalarLoggedAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            RaiseCommandExecuted(cmd.CommandText, sw.Elapsed, true, null);
            return result;
        }
        catch (SqliteException ex)
        {
            sw.Stop();
            RaiseCommandExecuted(cmd.CommandText, sw.Elapsed, false, ex.Message);
            throw new SqlExecutionException(cmd.CommandText, ex);
        }
    }

    /// <summary>Drains (and discards) every row of every result set a statement might produce, for utility statements whose row data (if any) is irrelevant.</summary>
    private async Task ExecuteDrainingAsync(SqliteCommand cmd, CancellationToken ct)
    {
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // intentionally discarded
            }
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));

        if (_editTransaction is not null)
        {
            IsDirty = true;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Schema discovery
    // ------------------------------------------------------------------------------------------

    /// <summary>Lists every attached database schema (main, temp, and any ATTACHed databases) via <c>PRAGMA database_list</c>.</summary>
    public async Task<IReadOnlyList<string>> GetAttachedSchemasAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        using var cmd = CreateCommand("PRAGMA database_list;");
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    /// <summary>
    /// Discovers every table, view, index and trigger across all attached schemas
    /// (<c>PRAGMA database_list</c> combined with each schema's <c>sqlite_master</c>).
    /// </summary>
    public async Task<IReadOnlyList<DatabaseObject>> GetDatabaseObjectsAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        var schemas = await GetAttachedSchemasAsync(ct).ConfigureAwait(false);
        var result = new List<DatabaseObject>();
        foreach (var schema in schemas)
        {
            string sql = $"SELECT type, name, tbl_name, sql FROM {SqlIdentifier.Quote(schema)}.sqlite_master ORDER BY type, name;";
            using var cmd = CreateCommand(sql);
            using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new DatabaseObject(
                    schema,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return result;
    }

    /// <summary>Reads column metadata for a table or view via <c>PRAGMA table_xinfo</c> (includes hidden/generated columns).</summary>
    public async Task<IReadOnlyList<ColumnDefinition>> GetColumnsAsync(string schema, string tableName, CancellationToken ct = default)
    {
        EnsureOpen();
        SqlIdentifier.Validate(schema, nameof(schema));
        SqlIdentifier.Validate(tableName, nameof(tableName));

        string sql = $"PRAGMA {SqlIdentifier.Quote(schema)}.table_xinfo({SqlIdentifier.Quote(tableName)});";
        using var cmd = CreateCommand(sql);
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
        var result = new List<ColumnDefinition>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ColumnDefinition(
                Id: reader.GetInt32(0),
                Name: reader.GetString(1),
                DataType: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                NotNull: reader.GetInt32(3) != 0,
                DefaultValue: reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture),
                PrimaryKeyOrder: reader.GetInt32(5),
                Hidden: reader.GetInt32(6) != 0));
        }

        return result;
    }

    private async Task<bool> SupportsRowIdAsync(string qualifiedTableName, CancellationToken ct)
    {
        // sqlite_master does not expose whether a table was declared WITHOUT ROWID, and views never
        // have a rowid, so probing is the only fully reliable detection method. The Ahtola provider
        // validates column existence lazily (on the first Read), unlike Microsoft.Data.Sqlite which
        // validates eagerly when the reader is produced, so the probe must read once to force it.
        // A "WHERE 0" no-op filter is used instead of "LIMIT 0": Ahtola has a documented limitation
        // where combining a "rowid" projection with "LIMIT 0" throws "no such column: rowid" even for
        // ordinary rowid tables, so "LIMIT 0" cannot be used to build a cheap zero-row probe here.
        try
        {
            using var cmd = CreateCommand($"SELECT rowid FROM {qualifiedTableName} WHERE 0;");
            using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
            // The "no such column: rowid" failure surfaces from Read, not from ExecuteReaderAsync
            // (which ExecuteReaderLoggedAsync already wraps as SqlExecutionException), so this
            // catches the provider's raw exception type as well.
            await reader.ReadAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SqlExecutionException or SqliteException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Paged table/view browsing + editing
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads one page of a table's or view's rows. <paramref name="filter"/> and <paramref name="orderBy"/>
    /// are raw SQL fragments (a boolean expression and an ORDER BY body, respectively) — they cannot be
    /// parameterized because they are expressions rather than values, so they are validated only to
    /// reject statement-stacking (<c>;</c>) and comments, not sanitized as if they were untrusted data.
    /// </summary>
    public async Task<TablePage> GetTablePageAsync(
        string schema,
        string tableName,
        int offset,
        int pageSize,
        string? filter = null,
        string? orderBy = null,
        CancellationToken ct = default)
    {
        EnsureOpen();
        SqlIdentifier.Validate(schema, nameof(schema));
        SqlIdentifier.Validate(tableName, nameof(tableName));
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset must not be negative.");
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");
        }

        if (!string.IsNullOrEmpty(filter))
        {
            SqlIdentifier.ValidateFilterExpression(filter);
        }

        if (!string.IsNullOrEmpty(orderBy))
        {
            SqlIdentifier.ValidateOrderByExpression(orderBy);
        }

        string qualified = SqlIdentifier.QuoteQualified(schema, tableName);
        IReadOnlyList<ColumnDefinition> columns;
        try
        {
            columns = await GetColumnsAsync(schema, tableName, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("no such table:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Table or view '{schema}.{tableName}' was not found.", ex);
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"Table or view '{schema}.{tableName}' was not found.");
        }

        bool hasRowId = await SupportsRowIdAsync(qualified, ct).ConfigureAwait(false);
        var pkColumns = columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).Select(c => c.Name).ToArray();

        RowIdentityKind identity;
        IReadOnlyList<string> keyColumns;
        if (hasRowId)
        {
            identity = RowIdentityKind.RowId;
            keyColumns = new[] { TablePage.RowIdColumnName };
        }
        else if (pkColumns.Length > 0)
        {
            identity = RowIdentityKind.PrimaryKey;
            keyColumns = pkColumns;
        }
        else
        {
            identity = RowIdentityKind.None;
            keyColumns = Array.Empty<string>();
        }

        string whereSql = string.IsNullOrEmpty(filter) ? string.Empty : $" WHERE ({filter})";
        string orderSql = string.IsNullOrEmpty(orderBy) ? string.Empty : $" ORDER BY {orderBy}";
        string rowIdSelect = identity == RowIdentityKind.RowId ? $"rowid AS {SqlIdentifier.Quote(TablePage.RowIdColumnName)}, " : string.Empty;
        string columnList = string.Join(", ", columns.Select(c => SqlIdentifier.Quote(c.Name)));

        long totalRows;
        using (var countCmd = CreateCommand($"SELECT COUNT(*) FROM {qualified}{whereSql};"))
        {
            var scalar = await ExecuteScalarLoggedAsync(countCmd, ct).ConfigureAwait(false);
            totalRows = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        var table = new DataTable(tableName);
        if (identity == RowIdentityKind.RowId)
        {
            table.Columns.Add(TablePage.RowIdColumnName, typeof(long));
        }

        foreach (var column in columns)
        {
            table.Columns.Add(column.Name, typeof(object));
        }

        string pageSql = $"SELECT {rowIdSelect}{columnList} FROM {qualified}{whereSql}{orderSql} LIMIT @pageSize OFFSET @offset;";
        using (var pageCmd = CreateCommand(pageSql))
        {
            pageCmd.Parameters.AddWithValue("@pageSize", pageSize);
            pageCmd.Parameters.AddWithValue("@offset", offset);
            using var reader = await ExecuteReaderLoggedAsync(pageCmd, ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new object?[reader.FieldCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }

                table.Rows.Add(values);
            }
        }

        table.AcceptChanges();

        return new TablePage
        {
            Schema = schema,
            TableName = tableName,
            IsReadOnly = IsReadOnly || identity == RowIdentityKind.None,
            Offset = offset,
            PageSize = pageSize,
            TotalRows = totalRows,
            Columns = columns,
            Data = table,
            Filter = filter,
            OrderBy = orderBy,
            Identity = identity,
            KeyColumns = keyColumns,
        };
    }

    private static string SqlColumnRef(string keyColumnName) =>
        keyColumnName == TablePage.RowIdColumnName ? "rowid" : SqlIdentifier.Quote(keyColumnName);

    /// <summary>
    /// Persists every pending insert/update/delete in <paramref name="page"/>'s <see cref="TablePage.Data"/>
    /// back to the database, using parameterized commands keyed by rowid or primary key. Participates in
    /// the active edit transaction if one is open (see <see cref="BeginEditAsync"/>); otherwise the whole
    /// batch is applied atomically in its own transaction. On success, <see cref="DataTable.AcceptChanges"/>
    /// is called so the page reflects the persisted state.
    /// </summary>
    /// <returns>The total number of rows affected.</returns>
    public async Task<int> ApplyChangesAsync(TablePage page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        EnsureWritable();
        if (page.IsReadOnly || page.Identity == RowIdentityKind.None)
        {
            throw new InvalidOperationException($"Table page '{page.Schema}.{page.TableName}' is read-only and cannot be persisted.");
        }

        var dataTable = page.Data;
        bool ownsTransaction = _editTransaction is null;
        SqliteTransaction? tx = ownsTransaction ? (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false) : _editTransaction;

        try
        {
            int affected = 0;

            foreach (DataRow row in dataTable.Select(null, null, DataViewRowState.Deleted))
            {
                affected += await DeleteRowAsync(page, row, tx, ct).ConfigureAwait(false);
            }

            foreach (DataRow row in dataTable.Select(null, null, DataViewRowState.ModifiedCurrent))
            {
                affected += await UpdateRowAsync(page, row, tx, ct).ConfigureAwait(false);
            }

            foreach (DataRow row in dataTable.Select(null, null, DataViewRowState.Added))
            {
                affected += await InsertRowAsync(page, row, tx, ct).ConfigureAwait(false);
            }

            if (ownsTransaction)
            {
                await tx!.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                IsDirty = true;
            }

            dataTable.AcceptChanges();
            return affected;
        }
        catch
        {
            if (ownsTransaction)
            {
                await tx!.RollbackAsync(ct).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (ownsTransaction)
            {
                await tx!.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<int> DeleteRowAsync(TablePage page, DataRow row, SqliteTransaction? tx, CancellationToken ct)
    {
        string qualified = SqlIdentifier.QuoteQualified(page.Schema, page.TableName);
        var whereParts = new List<string>();
        var cmd = _connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Transaction = tx;
            for (int i = 0; i < page.KeyColumns.Count; i++)
            {
                string key = page.KeyColumns[i];
                whereParts.Add($"{SqlColumnRef(key)} = @k{i}");
                cmd.Parameters.AddWithValue($"@k{i}", row[key, DataRowVersion.Original] ?? DBNull.Value);
            }

            cmd.CommandText = $"DELETE FROM {qualified} WHERE {string.Join(" AND ", whereParts)};";
            return await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> UpdateRowAsync(TablePage page, DataRow row, SqliteTransaction? tx, CancellationToken ct)
    {
        string qualified = SqlIdentifier.QuoteQualified(page.Schema, page.TableName);
        var cmd = _connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Transaction = tx;
            var setParts = new List<string>();
            int p = 0;
            foreach (var column in page.Columns)
            {
                setParts.Add($"{SqlIdentifier.Quote(column.Name)} = @p{p}");
                cmd.Parameters.AddWithValue($"@p{p}", row[column.Name, DataRowVersion.Current] ?? DBNull.Value);
                p++;
            }

            var whereParts = new List<string>();
            for (int i = 0; i < page.KeyColumns.Count; i++)
            {
                string key = page.KeyColumns[i];
                whereParts.Add($"{SqlColumnRef(key)} = @k{i}");
                cmd.Parameters.AddWithValue($"@k{i}", row[key, DataRowVersion.Original] ?? DBNull.Value);
            }

            cmd.CommandText = $"UPDATE {qualified} SET {string.Join(", ", setParts)} WHERE {string.Join(" AND ", whereParts)};";
            return await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);
        }
    }

    private async Task<int> InsertRowAsync(TablePage page, DataRow row, SqliteTransaction? tx, CancellationToken ct)
    {
        string qualified = SqlIdentifier.QuoteQualified(page.Schema, page.TableName);
        var cmd = _connection.CreateCommand();
        await using (cmd.ConfigureAwait(false))
        {
            cmd.Transaction = tx;

            // Columns left unset (DBNull) are omitted from the statement entirely so SQLite applies
            // the column's DEFAULT (or NULL, or auto-assigns rowid for an INTEGER PRIMARY KEY column).
            var columnNames = new List<string>();
            var paramNames = new List<string>();
            int p = 0;
            foreach (var column in page.Columns)
            {
                object? value = row[column.Name, DataRowVersion.Current];
                if (value is DBNull or null)
                {
                    continue;
                }

                columnNames.Add(SqlIdentifier.Quote(column.Name));
                string paramName = $"@p{p}";
                paramNames.Add(paramName);
                cmd.Parameters.AddWithValue(paramName, value);
                p++;
            }

            cmd.CommandText = columnNames.Count == 0
                ? $"INSERT INTO {qualified} DEFAULT VALUES;"
                : $"INSERT INTO {qualified} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", paramNames)});";

            int result = await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);

            if (page.Identity == RowIdentityKind.RowId)
            {
                using var idCmd = CreateCommand("SELECT last_insert_rowid();");
                idCmd.Transaction = tx;
                var newRowId = await ExecuteScalarLoggedAsync(idCmd, ct).ConfigureAwait(false);
                row[TablePage.RowIdColumnName] = Convert.ToInt64(newRowId, CultureInfo.InvariantCulture);
            }

            return result;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Arbitrary multi-statement SQL execution
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Executes one or more semicolon-separated SQL statements and returns the last result set
    /// produced (if any), the total rows affected across all statements, elapsed time, and a status
    /// message. Errors are surfaced as <see cref="SqlExecutionException"/>, never swallowed.
    /// </summary>
    public async Task<QueryResult> ExecuteSqlAsync(string sql, CancellationToken ct = default)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(sql);

        return await ExecuteMultiStatementAsync(sql, _editTransaction, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Core multi-statement execution loop shared by <see cref="ExecuteSqlAsync"/> and
    /// <see cref="ImportSqlScriptAsync(string,CancellationToken)"/>. Runs <paramref name="sql"/>
    /// attached to <paramref name="transaction"/> (which may be <c>null</c> to run in autocommit
    /// mode), consuming every result set the script produces.
    /// </summary>
    private async Task<QueryResult> ExecuteMultiStatementAsync(string sql, SqliteTransaction? transaction, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cmd = CreateCommand(sql, transaction);

        DataTable? lastResult = null;
        int rowsAffected = 0;

        try
        {
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            do
            {
                DataTable? current = null;
                if (reader.FieldCount > 0)
                {
                    current = new DataTable();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        current.Columns.Add(reader.GetName(i), typeof(object));
                    }

                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var values = new object?[reader.FieldCount];
                        for (int i = 0; i < values.Length; i++)
                        {
                            values[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                        }

                        current.Rows.Add(values);
                    }

                    current.AcceptChanges();
                }

                if (reader.RecordsAffected > 0)
                {
                    rowsAffected += reader.RecordsAffected;
                }

                if (current is not null)
                {
                    lastResult = current;
                }
            }
            while (await reader.NextResultAsync(ct).ConfigureAwait(false));
        }
        catch (SqliteException ex)
        {
            sw.Stop();
            RaiseCommandExecuted(sql, sw.Elapsed, false, ex.Message);
            throw new SqlExecutionException(sql, ex);
        }

        sw.Stop();
        RaiseCommandExecuted(sql, sw.Elapsed, true, null);
        if (_editTransaction is not null)
        {
            IsDirty = true;
        }

        string message = lastResult is null
            ? $"{rowsAffected} row(s) affected."
            : $"{lastResult.Rows.Count} row(s) returned.";

        return new QueryResult
        {
            Data = lastResult,
            RowsAffected = rowsAffected,
            Elapsed = sw.Elapsed,
            Message = message,
        };
    }

    // ------------------------------------------------------------------------------------------
    // PRAGMA read/write
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a pragma's result set (most pragmas return a single scalar row; some, like <c>table_info</c>,
    /// take a table-name argument and return one row per column).
    /// </summary>
    /// <param name="name">The pragma name, e.g. <c>"foreign_keys"</c> or <c>"table_info"</c>.</param>
    /// <param name="schema">Optional schema (database) to qualify the pragma with.</param>
    /// <param name="tableArgument">
    /// Optional identifier argument for table-scoped pragmas (e.g. the table name for <c>table_info</c>),
    /// rendered as <c>PRAGMA name(argument)</c>. Quoted like any other identifier.
    /// </param>
    public async Task<PragmaValue> GetPragmaAsync(string name, string? schema = null, string? tableArgument = null, CancellationToken ct = default)
    {
        EnsureOpen();
        SqlIdentifier.Validate(name, nameof(name));
        string qualified = schema is null ? SqlIdentifier.Quote(name) : SqlIdentifier.QuoteQualified(schema, name);
        string argumentSql = string.IsNullOrEmpty(tableArgument) ? string.Empty : $"({SqlIdentifier.Quote(tableArgument)})";
        string sql = $"PRAGMA {qualified}{argumentSql};";

        using var cmd = CreateCommand(sql);
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = reader.GetName(i);
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(values);
        }

        return new PragmaValue(name, columns, rows);
    }

    /// <summary>
    /// Sets a pragma's value. SQLite's grammar does not allow a bound parameter in a pragma's value
    /// position, so unlike every other value-accepting API on this type, <paramref name="value"/> is
    /// embedded directly into the statement (after rejecting NUL and ';'). Callers must supply a
    /// trusted literal (e.g. "ON", "OFF", "2", or a quoted string like "'WAL'").
    /// </summary>
    public async Task SetPragmaAsync(string name, string value, string? schema = null, CancellationToken ct = default)
    {
        EnsureOpen();
        SqlIdentifier.Validate(name, nameof(name));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Pragma value must not contain a NUL character.", nameof(value));
        }

        if (value.Contains(';'))
        {
            throw new ArgumentException("Pragma value must not contain a statement separator (';').", nameof(value));
        }

        string qualified = schema is null ? SqlIdentifier.Quote(name) : SqlIdentifier.QuoteQualified(schema, name);
        using var cmd = CreateCommand($"PRAGMA {qualified} = {value};");
        await ExecuteDrainingAsync(cmd, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------
    // Attach / Detach
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Attaches another database file under the given schema alias.
    /// </summary>
    /// <remarks>
    /// Ahtola limitation: unlike native SQLite, the Ahtola managed engine requires the primary
    /// connection itself to be file-backed in order to ATTACH another file — attaching from an
    /// <c>:memory:</c>-opened primary database throws a <see cref="Exceptions.SqlExecutionException"/>
    /// ("Managed ATTACH requires a file-backed managed primary database so attachments share its
    /// file system."). This is not silently worked around; open the primary session against a disk
    /// file (see <see cref="OpenAsync"/>/<see cref="CreateAsync"/>) before attaching.
    /// </remarks>
    public async Task AttachAsync(string path, string schemaName, CancellationToken ct = default)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(path);
        SqlIdentifier.Validate(schemaName, nameof(schemaName));

        using var cmd = CreateCommand($"ATTACH DATABASE @path AS {SqlIdentifier.Quote(schemaName)};");
        cmd.Parameters.AddWithValue("@path", path);
        await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Detaches a previously ATTACHed schema.</summary>
    public async Task DetachAsync(string schemaName, CancellationToken ct = default)
    {
        EnsureOpen();
        SqlIdentifier.Validate(schemaName, nameof(schemaName));
        using var cmd = CreateCommand($"DETACH DATABASE {SqlIdentifier.Quote(schemaName)};");
        await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------
    // Maintenance
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the database file to reclaim free space. When <paramref name="intoPath"/> is
    /// supplied, uses <c>VACUUM INTO</c> to write a compacted copy to a new file instead of
    /// rewriting the current one.
    /// </summary>
    /// <remarks>
    /// Ahtola limitation: <c>VACUUM INTO</c> requires the source connection to be file-backed under
    /// the Ahtola managed engine — running it against an <c>:memory:</c>-opened session throws a
    /// <see cref="Exceptions.SqlExecutionException"/> ("Managed VACUUM INTO requires a file-backed
    /// source database."). This is not silently worked around; open the session against a disk file
    /// (see <see cref="OpenAsync"/>/<see cref="CreateAsync"/>) before using <paramref name="intoPath"/>.
    /// </remarks>
    public async Task VacuumAsync(string? intoPath = null, CancellationToken ct = default)
    {
        EnsureWritable();
        if (intoPath is null)
        {
            using var cmd = CreateCommand("VACUUM;");
            await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);
        }
        else
        {
            if (File.Exists(intoPath))
            {
                File.Delete(intoPath);
            }

            using var cmd = CreateCommand("VACUUM INTO @path;");
            cmd.Parameters.AddWithValue("@path", intoPath);
            await ExecuteNonQueryLoggedAsync(cmd, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <c>PRAGMA integrity_check</c> and returns each reported message (a single "ok" means the database is healthy).</summary>
    public Task<IReadOnlyList<string>> IntegrityCheckAsync(CancellationToken ct = default) => RunTextPragmaCheckAsync("integrity_check", ct);

    /// <summary>Runs <c>PRAGMA quick_check</c> (a faster, less thorough variant of <see cref="IntegrityCheckAsync"/>).</summary>
    public Task<IReadOnlyList<string>> QuickCheckAsync(CancellationToken ct = default) => RunTextPragmaCheckAsync("quick_check", ct);

    private async Task<IReadOnlyList<string>> RunTextPragmaCheckAsync(string pragmaName, CancellationToken ct)
    {
        EnsureOpen();
        using var cmd = CreateCommand($"PRAGMA {pragmaName};");
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
        var messages = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return messages;
    }

    /// <summary>Runs <c>PRAGMA foreign_key_check</c> and returns every reported violation (empty when there are none).</summary>
    public async Task<IReadOnlyList<ForeignKeyViolation>> ForeignKeyCheckAsync(CancellationToken ct = default)
    {
        EnsureOpen();
        using var cmd = CreateCommand("PRAGMA foreign_key_check;");
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);
        var violations = new List<ForeignKeyViolation>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            violations.Add(new ForeignKeyViolation(
                Table: reader.GetString(0),
                RowId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Parent: reader.GetString(2),
                ForeignKeyId: reader.GetInt32(3)));
        }

        return violations;
    }

    /// <summary>Runs <c>PRAGMA optimize</c>, which lets SQLite decide whether to run ANALYZE-like maintenance based on usage.</summary>
    public async Task OptimizeAsync(CancellationToken ct = default)
    {
        EnsureWritable();
        using var cmd = CreateCommand("PRAGMA optimize;");
        await ExecuteDrainingAsync(cmd, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------
    // CSV import
    // ------------------------------------------------------------------------------------------

    private async Task<bool> TableExistsAsync(string schema, string tableName, CancellationToken ct)
    {
        using var cmd = CreateCommand($"SELECT 1 FROM {SqlIdentifier.Quote(schema)}.sqlite_master WHERE type IN ('table','view') AND name = @name LIMIT 1;");
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = await ExecuteScalarLoggedAsync(cmd, ct).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>Imports CSV data (RFC 4180: quoted fields, embedded delimiters/newlines, doubled-quote escaping) from a reader into a table, creating it if necessary.</summary>
    public async Task<CsvImportResult> ImportCsvAsync(string schema, string tableName, TextReader reader, CsvImportOptions options, CancellationToken ct = default)
    {
        EnsureWritable();
        SqlIdentifier.Validate(schema, nameof(schema));
        SqlIdentifier.Validate(tableName, nameof(tableName));
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();

        string[]? header = null;
        var dataRows = new List<string[]>();
        bool firstRecord = true;
        foreach (var record in CsvFormat.ReadRecords(reader, options.Delimiter))
        {
            ct.ThrowIfCancellationRequested();
            if (firstRecord && options.HasHeader)
            {
                header = record;
                firstRecord = false;
                continue;
            }

            firstRecord = false;
            dataRows.Add(record);
        }

        int columnCount = header?.Length ?? dataRows.Select(r => r.Length).DefaultIfEmpty(0).Max();
        if (columnCount == 0)
        {
            return new CsvImportResult(Array.Empty<string>(), 0, sw.Elapsed, false);
        }

        var columnNames = new string[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            columnNames[i] = header is not null && i < header.Length ? header[i] : $"column{i + 1}";
        }

        bool tableExists = await TableExistsAsync(schema, tableName, ct).ConfigureAwait(false);
        bool created = false;

        if (!tableExists)
        {
            if (!options.CreateTable)
            {
                throw new InvalidOperationException($"Table '{schema}.{tableName}' does not exist and {nameof(CsvImportOptions.CreateTable)} is false.");
            }

            string[] types = options.InferTypes
                ? InferColumnTypes(columnCount, dataRows)
                : Enumerable.Repeat("TEXT", columnCount).ToArray();

            string createSql = $"CREATE TABLE {SqlIdentifier.QuoteQualified(schema, tableName)} (" +
                string.Join(", ", columnNames.Select((c, i) => $"{SqlIdentifier.Quote(c)} {types[i]}")) + ");";

            using var createCmd = CreateCommand(createSql);
            await ExecuteNonQueryLoggedAsync(createCmd, ct).ConfigureAwait(false);
            created = true;
        }

        bool ownsTransaction = _editTransaction is null;
        SqliteTransaction? tx = ownsTransaction ? (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false) : _editTransaction;

        try
        {
            string insertSql = $"INSERT INTO {SqlIdentifier.QuoteQualified(schema, tableName)} " +
                $"({string.Join(", ", columnNames.Select(c => SqlIdentifier.Quote(c)))}) " +
                $"VALUES ({string.Join(", ", Enumerable.Range(0, columnCount).Select(i => "@p" + i))});";

            var insertCmd = _connection.CreateCommand();
            await using (insertCmd.ConfigureAwait(false))
            {
                insertCmd.CommandText = insertSql;
                insertCmd.Transaction = tx;
                var parameters = new SqliteParameter[columnCount];
                for (int i = 0; i < columnCount; i++)
                {
                    parameters[i] = new SqliteParameter();
                    parameters[i].ParameterName = "@p" + i;
                    insertCmd.Parameters.Add(parameters[i]);
                }

                int imported = 0;
                foreach (var record in dataRows)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < columnCount; i++)
                    {
                        string? raw = i < record.Length ? record[i] : null;
                        parameters[i].Value = ConvertCsvValue(raw);
                    }

                    await ExecuteNonQueryLoggedAsync(insertCmd, ct).ConfigureAwait(false);
                    imported++;
                }

                if (ownsTransaction)
                {
                    await tx!.CommitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    IsDirty = true;
                }

                sw.Stop();
                return new CsvImportResult(columnNames, imported, sw.Elapsed, created);
            }
        }
        catch
        {
            if (ownsTransaction)
            {
                await tx!.RollbackAsync(ct).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (ownsTransaction)
            {
                await tx!.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Convenience overload of <see cref="ImportCsvAsync(string,string,TextReader,CsvImportOptions,CancellationToken)"/> that reads directly from a file.</summary>
    public async Task<CsvImportResult> ImportCsvFileAsync(string schema, string tableName, string csvFilePath, CsvImportOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(csvFilePath);
        using var streamReader = new StreamReader(csvFilePath);
        return await ImportCsvAsync(schema, tableName, streamReader, options, ct).ConfigureAwait(false);
    }

    private static object ConvertCsvValue(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return DBNull.Value;
        }

        return raw;
    }

    private static string[] InferColumnTypes(int columnCount, List<string[]> dataRows)
    {
        var types = new string[columnCount];
        for (int c = 0; c < columnCount; c++)
        {
            bool anySample = false;
            bool allInteger = true;
            bool allReal = true;

            foreach (var record in dataRows)
            {
                if (c >= record.Length || string.IsNullOrEmpty(record[c]))
                {
                    continue;
                }

                anySample = true;
                string value = record[c];
                if (allInteger && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    allInteger = false;
                }

                if (allReal && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    allReal = false;
                }

                if (!allInteger && !allReal)
                {
                    break;
                }
            }

            types[c] = !anySample ? "TEXT" : allInteger ? "INTEGER" : allReal ? "REAL" : "TEXT";
        }

        return types;
    }

    // ------------------------------------------------------------------------------------------
    // CSV / JSON export
    // ------------------------------------------------------------------------------------------

    /// <summary>Executes <paramref name="sql"/> and streams the result set to <paramref name="writer"/> as RFC 4180 CSV.</summary>
    /// <returns>The number of data rows written.</returns>
    public async Task<int> ExportCsvAsync(string sql, TextWriter writer, CsvExportOptions options, CancellationToken ct = default)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(sql);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);

        using var cmd = CreateCommand(sql);
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);

        if (options.IncludeHeader)
        {
            var headers = new string[reader.FieldCount];
            for (int i = 0; i < headers.Length; i++)
            {
                headers[i] = reader.GetName(i);
            }

            await CsvFormat.WriteRecordAsync(writer, headers, options.Delimiter, ct).ConfigureAwait(false);
        }

        int rowCount = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var fields = new string?[reader.FieldCount];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = FormatCellAsText(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }

            await CsvFormat.WriteRecordAsync(writer, fields, options.Delimiter, ct).ConfigureAwait(false);
            rowCount++;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return rowCount;
    }

    /// <summary>Convenience overload of <see cref="ExportCsvAsync(string,TextWriter,CsvExportOptions,CancellationToken)"/> that writes directly to a file.</summary>
    public async Task<int> ExportCsvFileAsync(string sql, string destinationPath, CsvExportOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var streamWriter = new StreamWriter(destinationPath, append: false);
        return await ExportCsvAsync(sql, streamWriter, options, ct).ConfigureAwait(false);
    }

    private static string? FormatCellAsText(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Executes <paramref name="sql"/> and streams the result set to <paramref name="destination"/> as a JSON array of objects.</summary>
    /// <returns>The number of rows written.</returns>
    public async Task<int> ExportJsonAsync(string sql, Stream destination, CancellationToken ct = default)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(sql);
        ArgumentNullException.ThrowIfNull(destination);

        using var cmd = CreateCommand(sql);
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);

        await using var jsonWriter = new Utf8JsonWriter(destination);
        jsonWriter.WriteStartArray();

        int rowCount = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            jsonWriter.WriteStartObject();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                if (reader.IsDBNull(i))
                {
                    jsonWriter.WriteNull(name);
                    continue;
                }

                object value = reader.GetValue(i);
                switch (value)
                {
                    case long l:
                        jsonWriter.WriteNumber(name, l);
                        break;
                    case double d:
                        jsonWriter.WriteNumber(name, d);
                        break;
                    case string s:
                        jsonWriter.WriteString(name, s);
                        break;
                    case byte[] bytes:
                        jsonWriter.WriteString(name, Convert.ToBase64String(bytes));
                        break;
                    default:
                        jsonWriter.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            jsonWriter.WriteEndObject();
            rowCount++;
        }

        jsonWriter.WriteEndArray();
        await jsonWriter.FlushAsync(ct).ConfigureAwait(false);
        return rowCount;
    }

    /// <summary>Convenience overload of <see cref="ExportJsonAsync(string,Stream,CancellationToken)"/> that writes directly to a file.</summary>
    public async Task<int> ExportJsonFileAsync(string sql, string destinationPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await using (fs.ConfigureAwait(false))
        {
            return await ExportJsonAsync(sql, fs, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------
    // SQL dump export / SQL script import
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a textual SQL dump of the <c>main</c> schema (schema DDL plus row data as parameterizable-shaped
    /// INSERT statements), similar in spirit to the <c>sqlite3</c> CLI's <c>.dump</c> command.
    /// </summary>
    public async Task ExportSqlDumpAsync(TextWriter writer, CancellationToken ct = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(writer);

        await writer.WriteLineAsync("PRAGMA foreign_keys=OFF;").ConfigureAwait(false);
        await writer.WriteLineAsync("BEGIN TRANSACTION;").ConfigureAwait(false);

        var objects = new List<(string Type, string Name, string? Sql)>();
        using (var cmd = CreateCommand(
            // Ahtola's sqlite_master/sqlite_schema catalog does not expose an implicit rowid column
            // (unlike native SQLite), so object order falls back to name within each type bucket
            // instead of original creation order.
            "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL " +
            "ORDER BY (CASE type WHEN 'table' THEN 0 ELSE 1 END), name;"))
        using (var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                objects.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var obj in objects.Where(o => o.Type == "table" && !o.Name.StartsWith("sqlite_", StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            await WriteStatementAsync(writer, obj.Sql!, ct).ConfigureAwait(false);
            await DumpTableRowsAsync(writer, obj.Name, ct).ConfigureAwait(false);
        }

        foreach (var obj in objects.Where(o => o.Type != "table" && !o.Name.StartsWith("sqlite_", StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();
            await WriteStatementAsync(writer, obj.Sql!, ct).ConfigureAwait(false);
        }

        await writer.WriteLineAsync("COMMIT;").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteStatementAsync(TextWriter writer, string sqlText, CancellationToken ct)
    {
        string trimmed = sqlText.TrimEnd();
        await writer.WriteLineAsync((trimmed.EndsWith(';') ? trimmed : trimmed + ";").AsMemory(), ct).ConfigureAwait(false);
    }

    private async Task DumpTableRowsAsync(TextWriter writer, string tableName, CancellationToken ct)
    {
        string quotedTable = SqlIdentifier.Quote(tableName);
        using var cmd = CreateCommand($"SELECT * FROM {quotedTable};");
        using var reader = await ExecuteReaderLoggedAsync(cmd, ct).ConfigureAwait(false);

        var columnNames = new string[reader.FieldCount];
        for (int i = 0; i < columnNames.Length; i++)
        {
            columnNames[i] = reader.GetName(i);
        }

        string columnList = string.Join(", ", columnNames.Select(c => SqlIdentifier.Quote(c)));

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new string[reader.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = FormatSqlLiteral(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }

            await writer.WriteLineAsync($"INSERT INTO {quotedTable} ({columnList}) VALUES ({string.Join(", ", values)});".AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private static string FormatSqlLiteral(object? value) => value switch
    {
        null or DBNull => "NULL",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        string s => "'" + s.Replace("'", "''") + "'",
        _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''") + "'",
    };

    /// <summary>Convenience overload of <see cref="ExportSqlDumpAsync(TextWriter,CancellationToken)"/> that writes directly to a file.</summary>
    public async Task ExportSqlDumpFileAsync(string destinationPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var streamWriter = new StreamWriter(destinationPath, append: false);
        await ExportSqlDumpAsync(streamWriter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a multi-statement SQL script (e.g. a previously exported dump) against this session.
    /// </summary>
    /// <remarks>
    /// If the script is wrapped in its own leading <c>BEGIN [TRANSACTION];</c> / trailing
    /// <c>COMMIT;</c> or <c>END [TRANSACTION];</c> pair — the shape <see cref="ExportSqlDumpAsync(TextWriter,CancellationToken)"/>
    /// produces — that outer wrapper is recognized and removed before execution (see
    /// <see cref="SqlScriptSplitter.RemoveOuterTransactionWrapper"/>); only comments, string
    /// literals, and inner statements (including trigger bodies) are preserved untouched. This
    /// matters for two reasons:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// If a long-lived edit transaction is active (see <see cref="BeginEditAsync"/>), executing the
    /// script's own literal "COMMIT;" would commit the caller's ambient, possibly unrelated pending
    /// edits, and its literal "BEGIN TRANSACTION;" would fail outright because SQLite does not
    /// support nested transactions. Instead, the script's statements are executed as part of the
    /// existing ambient transaction, and its commit/revert stays entirely under the caller's control.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// If there is no ambient edit transaction, this method still guarantees the import itself is
    /// atomic (all statements succeed, or none of their effects persist) by running the unwrapped
    /// statements inside a transaction it manages itself — this holds even for scripts that never
    /// had their own BEGIN/COMMIT wrapper.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public async Task<QueryResult> ImportSqlScriptAsync(string sqlScript, CancellationToken ct = default)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrEmpty(sqlScript);

        string body = SqlScriptSplitter.RemoveOuterTransactionWrapper(sqlScript);

        if (_editTransaction is not null)
        {
            // Attach to the caller's ambient edit transaction, exactly like any other mutating
            // operation performed while an edit is in progress. Its commit/revert is entirely the
            // caller's decision — this method never commits or rolls it back.
            return await ExecuteMultiStatementAsync(body, _editTransaction, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new QueryResult { Data = null, RowsAffected = 0, Elapsed = TimeSpan.Zero, Message = "0 row(s) affected." };
        }

        // No ambient transaction: guarantee the import itself is all-or-nothing by managing a
        // dedicated local transaction, independent of the long-lived edit-transaction concept.
        var localTransaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ExecuteMultiStatementAsync(body, localTransaction, ct).ConfigureAwait(false);
            await localTransaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await localTransaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await localTransaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Convenience overload of <see cref="ImportSqlScriptAsync(string,CancellationToken)"/> that reads the script from a file.</summary>
    public async Task<QueryResult> ImportSqlScriptFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        string script = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        return await ImportSqlScriptAsync(script, ct).ConfigureAwait(false);
    }
}
