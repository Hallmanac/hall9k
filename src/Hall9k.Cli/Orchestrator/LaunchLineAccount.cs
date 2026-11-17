using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// Puts a project's bound Claude Code account on a line a person pastes. Hall9k starts nothing from such a
/// line, so the account has to ride on the line itself. In POSIX it is <c>CLAUDE_CONFIG_DIR='&lt;dir&gt;' claude ...</c>,
/// where an assignment before the command word applies to that command alone. PowerShell has no inline form,
/// and a bare <c>$env:</c> assignment would stay set for the rest of that shell, so every later line pasted there
/// (an unbound project's, the node window's) would run and bill under this account. There the statement holding
/// the claude command is wrapped instead:
/// <c>try { $env:CLAUDE_CONFIG_DIR='&lt;dir&gt;'; claude ... } finally { Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction Ignore }</c>,
/// which clears the variable however the command ends, Ctrl+C included. Wrapping the whole statement rather than
/// inserting before <c>claude</c> also keeps <c>cd x &amp;&amp; claude</c> guarded by its <c>cd</c> and keeps
/// <c>echo hi | claude -p</c> parseable, since PowerShell allows no assignment after <c>&amp;&amp;</c> or <c>|</c>.
/// A line for an unbound project gets nothing, so it stays byte-identical.
/// <para>
/// Applied where a line is printed, to a stored launch text and a computed default alike, and never stored:
/// <c>launch-text set</c> refuses any text naming the variable (<see cref="RefuseStoredAccount"/>).
/// </para>
/// </summary>
public static class LaunchLineAccount
{
    private const string ClaudeWord = "claude";

    /// <summary>
    /// What goes in front of the claude command's statement to set <paramref name="configDirectory"/> for it,
    /// single-quoted so any path survives: a quote inside it is <c>'\''</c> in POSIX and <c>''</c> in PowerShell.
    /// In POSIX it sits immediately before <c>claude</c>; in PowerShell it opens the <c>try</c> block that
    /// <see cref="Suffix"/> closes.
    /// </summary>
    public static string Prefix(string configDirectory, LaunchShell shell) => shell == LaunchShell.PowerShell
        ? $"try {{ $env:{ClaudeConfigDirectory.Variable}='{configDirectory.Replace("'", "''")}'; "
        : $"{ClaudeConfigDirectory.Variable}='{configDirectory.Replace("'", "'\\''")}' ";

    /// <summary>
    /// What goes after the claude command's statement: nothing in POSIX, where the assignment never outlives
    /// the command, and in PowerShell the <c>finally</c> that clears the variable again.
    /// </summary>
    public static string Suffix(LaunchShell shell) => shell == LaunchShell.PowerShell
        ? $" }} finally {{ Remove-Item Env:{ClaudeConfigDirectory.Variable} -ErrorAction Ignore }}"
        : string.Empty;

    /// <summary>
    /// <paramref name="line"/> carrying the account on its first claude command word, or null when it has none
    /// (<see cref="FindClaudeCommandWord"/>). Unchanged when <paramref name="configDirectory"/> is null, which is
    /// the default login.
    /// </summary>
    public static string? Apply(string line, string? configDirectory, LaunchShell shell)
    {
        if (configDirectory is null)
        {
            return line;
        }

        if (Locate(line, shell) is not { } found)
        {
            return null;
        }

        if (shell != LaunchShell.PowerShell)
        {
            return line.Insert(found.Word, Prefix(configDirectory, shell));
        }

        int end = StatementEnd(line, found.Word, shell);
        return line[..found.StatementStart]
            + Prefix(configDirectory, shell)
            + line[found.StatementStart..end]
            + Suffix(shell)
            + line[end..];
    }

    /// <summary>
    /// The window line to print for <paramref name="launchText"/>: carrying the account when it is a claude-code
    /// text and the scope is bound, with a notice beside it when a bound claude-code text cannot be given one,
    /// because it has no claude command word or because it already names the variable itself. A text for any
    /// other CLI never gets the account.
    /// </summary>
    public static PrintedLaunchLine ForWindow(LaunchText launchText, string? configDirectory, LaunchShell shell)
    {
        if (configDirectory is null
            || !string.Equals(LaunchText.NormalizeCli(launchText.Cli), LaunchText.DefaultCli, StringComparison.Ordinal))
        {
            return new PrintedLaunchLine(launchText.Text, null);
        }

        if (NamesVariable(launchText.Text))
        {
            return new PrintedLaunchLine(launchText.Text, StoredAccountNotice(configDirectory));
        }

        return Apply(launchText.Text, configDirectory, shell) is { } prefixed
            ? new PrintedLaunchLine(prefixed, null)
            : new PrintedLaunchLine(launchText.Text, NoCommandWordNotice(configDirectory));
    }

    /// <summary>
    /// Said beneath a bound project's stored line that already names <c>CLAUDE_CONFIG_DIR</c>, which a text stored
    /// before <c>launch-text set</c> refused one can: Hall9k adds nothing to it, so it runs under whatever it names.
    /// </summary>
    public static string StoredAccountNotice(string configDirectory) =>
        $"This stored line sets {ClaudeConfigDirectory.Variable} itself, so Hall9k added nothing to it and it runs under "
        + $"whatever directory it names, which may not be the project's bound account ({configDirectory}). Store it again "
        + $"without {ClaudeConfigDirectory.Variable} with h9k orchestrator launch-text set, and the bound account is "
        + "added each time it is printed.";

    /// <summary>Said beneath a bound project's stored line when no claude command could be found in it to carry the account.</summary>
    public static string NoCommandWordNotice(string configDirectory) =>
        "This line does not carry the project's Claude Code account, because no claude command could be found in it "
        + $"to put {ClaudeConfigDirectory.Variable} on, so set {ClaudeConfigDirectory.Variable} to {configDirectory} "
        + "yourself before you run it.";

    /// <summary>
    /// Refuses a launch text naming <c>CLAUDE_CONFIG_DIR</c>: the account comes from the project's binding and
    /// is added when the line is printed, so a stored copy would either duplicate it or go stale on a rebind.
    /// </summary>
    public static void RefuseStoredAccount(string text)
    {
        if (NamesVariable(text))
        {
            throw new DomainValidationException(
                $"A launch text cannot set {ClaudeConfigDirectory.Variable}: the Claude Code account comes from the "
                + "project's binding and is added when the line is printed. Bind the project with "
                + "h9k project set <p> --claude-code-account <connection|default> instead. A line copied from "
                + $"h9k orchestrator launch-text show must have its {ClaudeConfigDirectory.Variable} prefix removed "
                + "before it is set.");
        }
    }

    /// <summary>
    /// Where the first claude command word in <paramref name="line"/> starts, or null: the first unquoted word
    /// that is exactly <c>claude</c> and either begins the line (after any spaces) or follows <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>;</c>, or <c>|</c> and any spaces. Quotes are tracked for the shell the line is written
    /// for, so a <c>claude</c> inside a quoted argument, or an operator inside one, is never taken.
    /// </summary>
    public static int? FindClaudeCommandWord(string line, LaunchShell shell) => Locate(line, shell)?.Word;

    private static bool NamesVariable(string text) =>
        text.Contains(ClaudeConfigDirectory.Variable, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first claude command word (<see cref="FindClaudeCommandWord"/>) and where its statement starts: the
    /// first non-space character after the last unquoted <c>;</c> before it, or the line's own start.
    /// </summary>
    private static (int Word, int StatementStart)? Locate(string line, LaunchShell shell)
    {
        char escape = EscapeOf(shell);
        bool commandPosition = true;
        int? statementStart = null;
        char? quote = null;
        for (int index = 0; index < line.Length; index++)
        {
            char current = line[index];
            if (quote is { } open)
            {
                if (current == escape && open == '"' && index + 1 < line.Length)
                {
                    index++;
                }
                else if (current == open)
                {
                    quote = null;
                }

                continue;
            }

            if (current is ' ' or '\t')
            {
                continue;
            }

            statementStart ??= index;
            switch (current)
            {
                case ';':
                    commandPosition = true;
                    statementStart = null;
                    continue;
                case '&' when index + 1 < line.Length && line[index + 1] == '&':
                case '|' when index + 1 < line.Length && line[index + 1] == '|':
                    commandPosition = true;
                    index++;
                    continue;
                case '|':
                    commandPosition = true;
                    continue;
                case '\'' or '"':
                    quote = current;
                    commandPosition = false;
                    continue;
            }

            if (current == escape)
            {
                index++;
                commandPosition = false;
                continue;
            }

            if (commandPosition && IsClaudeWordAt(line, index))
            {
                return (index, statementStart.Value);
            }

            commandPosition = false;
        }

        return null;
    }

    /// <summary>
    /// Where the statement holding the word at <paramref name="from"/> ends: at the next unquoted <c>;</c>, line
    /// break, or comment (<c>#</c> after a space), with any spaces before it left outside, or at the line's end.
    /// </summary>
    private static int StatementEnd(string line, int from, LaunchShell shell)
    {
        char escape = EscapeOf(shell);
        char? quote = null;
        int end = line.Length;
        for (int index = from; index < line.Length; index++)
        {
            char current = line[index];
            if (quote is { } open)
            {
                if (current == escape && open == '"' && index + 1 < line.Length)
                {
                    index++;
                }
                else if (current == open)
                {
                    quote = null;
                }

                continue;
            }

            if (current == escape)
            {
                index++;
            }
            else if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current is ';' or '\n' or '\r' || (current == '#' && line[index - 1] is ' ' or '\t'))
            {
                end = index;
                break;
            }
        }

        while (end > from && line[end - 1] is ' ' or '\t')
        {
            end--;
        }

        return end;
    }

    private static char EscapeOf(LaunchShell shell) => shell == LaunchShell.PowerShell
        ? '`'
        : '\\';

    private static bool IsClaudeWordAt(string line, int index)
    {
        if (string.CompareOrdinal(line, index, ClaudeWord, 0, ClaudeWord.Length) != 0)
        {
            return false;
        }

        int end = index + ClaudeWord.Length;
        return end == line.Length || line[end] is ' ' or '\t' or ';' or '|' or '&';
    }
}

/// <param name="Line">The line to paste.</param>
/// <param name="Notice">One sentence to print beneath it, or null.</param>
public sealed record PrintedLaunchLine(string Line, string? Notice);
