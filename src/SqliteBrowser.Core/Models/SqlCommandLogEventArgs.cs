namespace SqliteBrowser.Core.Models;

/// <summary>Event data raised by <see cref="Services.DatabaseSession.CommandExecuted"/> for every SQL command run against a session.</summary>
/// <param name="CommandText">The SQL text that was executed.</param>
/// <param name="Timestamp">When execution completed (UTC).</param>
/// <param name="Elapsed">How long execution took.</param>
/// <param name="Success">Whether the command completed without error.</param>
/// <param name="Error">The error message when <paramref name="Success"/> is <c>false</c>; otherwise <c>null</c>.</param>
public sealed record SqlCommandLogEntry(string CommandText, DateTimeOffset Timestamp, TimeSpan Elapsed, bool Success, string? Error);

/// <summary>Event args wrapping a <see cref="SqlCommandLogEntry"/>.</summary>
public sealed class SqlCommandLogEventArgs(SqlCommandLogEntry entry) : EventArgs
{
    public SqlCommandLogEntry Entry { get; } = entry;
}
