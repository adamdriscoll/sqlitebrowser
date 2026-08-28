namespace SqliteBrowser.Core.Exceptions;

/// <summary>
/// Thrown when a SQL statement fails to execute. Carries the offending SQL text (or a snippet of it)
/// alongside the underlying provider error so callers get an actionable, non-swallowed error.
/// </summary>
public sealed class SqlExecutionException : Exception
{
    public string CommandText { get; }

    public SqlExecutionException(string commandText, Exception innerException)
        : base(BuildMessage(commandText, innerException), innerException)
    {
        CommandText = commandText;
    }

    private static string BuildMessage(string commandText, Exception innerException)
    {
        var snippet = commandText.Length > 200 ? commandText[..200] + "..." : commandText;
        return $"Error executing SQL: {innerException.Message}{Environment.NewLine}Statement: {snippet}";
    }
}
