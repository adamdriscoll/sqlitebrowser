using System.Text.RegularExpressions;

namespace SqliteBrowser.Core.Services;

/// <summary>
/// Splits a multi-statement SQL script into individual top-level statements, and strips a
/// leading/trailing transaction-control wrapper (<c>BEGIN [TRANSACTION];</c> ... <c>COMMIT;</c> or
/// <c>END [TRANSACTION];</c>) such as the one <see cref="DatabaseSession.ExportSqlDumpAsync"/> emits
/// around every dump.
/// </summary>
/// <remarks>
/// This is a lightweight lexical scanner, not a full SQL parser — but it is not naive string
/// replacement either. It tracks single-quoted string literals, double-quoted/backtick/bracket
/// quoted identifiers, line (<c>--</c>) and block (<c>/* */</c>) comments, and <c>BEGIN...END</c>
/// nesting for compound statement bodies (e.g. <c>CREATE TRIGGER</c>). That is enough to find only
/// genuine top-level statement boundaries, so a <c>COMMIT</c> mentioned in a comment, a string
/// literal, or the body of a trigger is never mistaken for the script's own outer wrapper, and a
/// trigger's <c>BEGIN ... END;</c> body is never split apart.
/// </remarks>
internal static class SqlScriptSplitter
{
    private static readonly Regex BeginTransactionRegex =
        new(@"^\s*BEGIN(\s+(DEFERRED|IMMEDIATE|EXCLUSIVE))?(\s+TRANSACTION)?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommitOrEndTransactionRegex =
        new(@"^\s*(COMMIT|END)(\s+TRANSACTION)?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// If <paramref name="script"/> contains a <c>BEGIN [TRANSACTION];</c> statement followed,
    /// after zero or more interior statements, by a matching trailing
    /// <c>COMMIT;</c>/<c>END [TRANSACTION];</c> as the script's last statement — the shape produced
    /// by <see cref="DatabaseSession.ExportSqlDumpAsync(TextWriter,CancellationToken)"/>, typically
    /// with a leading <c>PRAGMA foreign_keys=OFF;</c> preamble before the BEGIN — returns the script
    /// with just that BEGIN/COMMIT pair removed. Any preamble and all interior statements are kept
    /// verbatim, in their original order. If no such single, unambiguous wrapper is found (e.g. the
    /// script has no transaction-control statements, or has more than one such pair), the original
    /// script is returned unchanged.
    /// </summary>
    public static string RemoveOuterTransactionWrapper(string script)
    {
        var statements = SplitTopLevelStatements(script);

        int beginIndex = -1;
        for (int idx = 0; idx < statements.Count; idx++)
        {
            if (string.IsNullOrWhiteSpace(statements[idx]))
            {
                continue;
            }

            if (BeginTransactionRegex.IsMatch(statements[idx]))
            {
                beginIndex = idx;
                break;
            }

            if (CommitOrEndTransactionRegex.IsMatch(statements[idx]))
            {
                // A COMMIT/END appears before any BEGIN — not a shape this method recognizes.
                return script;
            }
        }

        if (beginIndex < 0)
        {
            return script;
        }

        int lastIndex = statements.Count - 1;
        while (lastIndex > beginIndex && string.IsNullOrWhiteSpace(statements[lastIndex]))
        {
            lastIndex--;
        }

        if (lastIndex <= beginIndex || !CommitOrEndTransactionRegex.IsMatch(statements[lastIndex]))
        {
            return script;
        }

        // Only the simple single-wrapper shape ExportSqlDumpAsync produces is normalized here: if
        // any interior statement is itself a transaction-control statement, the script contains
        // more than one transaction block, which is ambiguous to unwrap safely, so it is left as-is.
        for (int idx = beginIndex + 1; idx < lastIndex; idx++)
        {
            if (BeginTransactionRegex.IsMatch(statements[idx]) || CommitOrEndTransactionRegex.IsMatch(statements[idx]))
            {
                return script;
            }
        }

        var kept = new List<string>(statements.Count - 2);
        for (int idx = 0; idx < statements.Count; idx++)
        {
            if (idx == beginIndex || idx == lastIndex)
            {
                continue;
            }

            kept.Add(statements[idx]);
        }

        return string.Concat(kept);
    }

    /// <summary>
    /// Splits <paramref name="script"/> into top-level statements, each including its own
    /// terminating <c>;</c> (the final statement's terminator is optional). Whitespace, comments,
    /// and quoted/string content are preserved verbatim inside each returned statement.
    /// </summary>
    public static List<string> SplitTopLevelStatements(string script)
    {
        var result = new List<string>();
        int n = script.Length;
        int start = 0;
        int compoundDepth = 0; // BEGIN...END nesting depth (trigger/compound statement bodies)
        bool statementHasToken = false; // has a real token appeared since the last top-level ';'?
        int i = 0;

        while (i < n)
        {
            char c = script[i];

            // Line comment: "-- ... \n"
            if (c == '-' && i + 1 < n && script[i + 1] == '-')
            {
                int nl = script.IndexOf('\n', i + 2);
                i = nl < 0 ? n : nl + 1;
                continue;
            }

            // Block comment: "/* ... */"
            if (c == '/' && i + 1 < n && script[i + 1] == '*')
            {
                int end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }

            // Single-quoted string literal; '' is an escaped embedded quote.
            if (c == '\'')
            {
                i = SkipQuoted(script, i, '\'');
                statementHasToken = true;
                continue;
            }

            // Double-quoted identifier; "" is an escaped embedded quote.
            if (c == '"')
            {
                i = SkipQuoted(script, i, '"');
                statementHasToken = true;
                continue;
            }

            // Backtick-quoted identifier (MySQL-style, also accepted by SQLite).
            if (c == '`')
            {
                i = SkipQuoted(script, i, '`');
                statementHasToken = true;
                continue;
            }

            // Bracket-quoted identifier (SQL Server-style, also accepted by SQLite).
            if (c == '[')
            {
                int close = script.IndexOf(']', i + 1);
                i = close < 0 ? n : close + 1;
                statementHasToken = true;
                continue;
            }

            if (c == ';' && compoundDepth == 0)
            {
                result.Add(script[start..(i + 1)]);
                i++;
                start = i;
                statementHasToken = false;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int wordStart = i;
                while (i < n && (char.IsLetterOrDigit(script[i]) || script[i] == '_'))
                {
                    i++;
                }

                string word = script[wordStart..i];
                bool isFirstTokenOfStatement = !statementHasToken;

                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    // "BEGIN" as the very first token of a top-level statement is a transaction
                    // control statement (terminated by its own ';', no matching END required).
                    // "BEGIN" anywhere else opens a compound statement body (e.g. CREATE TRIGGER's
                    // "... BEGIN ... END;") whose interior ';' characters are not statement breaks.
                    if (!isFirstTokenOfStatement)
                    {
                        compoundDepth++;
                    }
                }
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase) && compoundDepth > 0)
                {
                    compoundDepth--;
                }

                statementHasToken = true;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                statementHasToken = true;
            }

            i++;
        }

        if (start < n && !string.IsNullOrWhiteSpace(script[start..]))
        {
            result.Add(script[start..]);
        }

        return result;
    }

    private static int SkipQuoted(string script, int openingIndex, char quoteChar)
    {
        int i = openingIndex + 1;
        int n = script.Length;
        while (i < n)
        {
            if (script[i] == quoteChar)
            {
                if (i + 1 < n && script[i + 1] == quoteChar)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return n;
    }
}
