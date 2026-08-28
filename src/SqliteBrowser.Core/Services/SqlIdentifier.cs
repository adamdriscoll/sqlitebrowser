namespace SqliteBrowser.Core.Services;

/// <summary>
/// Helpers for safely quoting SQLite identifiers (schema/table/column names) and for validating
/// user-supplied filter/order-by fragments that cannot be parameterized because they are raw SQL
/// expressions rather than values.
/// </summary>
public static class SqlIdentifier
{
    /// <summary>
    /// Validates that <paramref name="identifier"/> is non-empty and free of NUL characters.
    /// SQLite identifiers are otherwise unrestricted, but embedding NUL would allow a truncated,
    /// attacker-controlled string to be interpreted as a different (shorter) identifier by the engine.
    /// </summary>
    public static void Validate(string identifier, string paramName)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("Identifier must not be null or empty.", paramName);
        }

        if (identifier.Contains('\0'))
        {
            throw new ArgumentException("Identifier must not contain a NUL character.", paramName);
        }
    }

    /// <summary>Quotes a single identifier using double quotes, doubling any embedded quote characters.</summary>
    public static string Quote(string identifier, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(identifier))] string? paramName = null)
    {
        Validate(identifier, paramName ?? nameof(identifier));
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Quotes a schema-qualified identifier, e.g. <c>"main"."my table"</c>.</summary>
    public static string QuoteQualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    /// <summary>
    /// Validates a raw SQL boolean expression intended to be spliced into a WHERE clause (e.g. a
    /// user-typed table filter). This is deliberately NOT sanitization or parameterization — the
    /// expression is treated as trusted, advanced SQL authored by the user of the application,
    /// exactly like a filter box in a desktop SQLite browser. The only protection applied here is
    /// rejecting statement-stacking (<c>;</c>) and comment tokens that could be used to smuggle a
    /// second statement past a naive editor, plus the same NUL-byte guard used for identifiers.
    /// </summary>
    public static void ValidateFilterExpression(string expression)
    {
        if (expression.Contains('\0'))
        {
            throw new ArgumentException("Filter expression must not contain a NUL character.", nameof(expression));
        }

        if (expression.Contains(';'))
        {
            throw new ArgumentException("Filter expression must not contain a statement separator (';'). Only a single boolean expression is allowed.", nameof(expression));
        }

        if (expression.Contains("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Filter expression must not contain a line comment ('--').", nameof(expression));
        }

        if (expression.Contains("/*", StringComparison.Ordinal) || expression.Contains("*/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Filter expression must not contain a block comment ('/*' or '*/').", nameof(expression));
        }
    }

    /// <summary>
    /// Validates a raw SQL ORDER BY clause body (without the leading "ORDER BY"). Subject to the
    /// same statement-stacking/comment restrictions as <see cref="ValidateFilterExpression"/>.
    /// </summary>
    public static void ValidateOrderByExpression(string expression) => ValidateFilterExpression(expression);
}
