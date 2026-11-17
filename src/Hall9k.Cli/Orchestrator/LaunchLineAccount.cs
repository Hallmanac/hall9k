using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// Puts a project's bound Claude Code account on a line a person pastes. Hall9k starts nothing from such a
/// line, so the account has to ride on the line itself: <c>CLAUDE_CONFIG_DIR='&lt;dir&gt;' claude ...</c> in
/// POSIX, where an assignment before the command word applies to that command alone, and
/// <c>$env:CLAUDE_CONFIG_DIR='&lt;dir&gt;'; claude ...</c> in PowerShell, which has no inline form and so sets
/// it for the rest of that shell. A line for an unbound project gets nothing, so it stays byte-identical.
/// <para>
/// Applied where a line is printed, to a stored launch text and a computed default alike, and never stored:
/// <c>launch-text set</c> refuses any text naming the variable (<see cref="RefuseStoredAccount"/>).
/// </para>
/// </summary>
public static class LaunchLineAccount
{
    private const string ClaudeWord = "claude";

    /// <summary>
    /// The prefix that sets <paramref name="configDirectory"/> for the claude command it is written in front
    /// of, single-quoted so any path survives: a quote inside it is <c>'\''</c> in POSIX and <c>''</c> in PowerShell.
    /// </summary>
    public static string Prefix(string configDirectory, LaunchShell shell) => shell == LaunchShell.PowerShell
        ? $"$env:{ClaudeConfigDirectory.Variable}='{configDirectory.Replace("'", "''")}'; "
        : $"{ClaudeConfigDirectory.Variable}='{configDirectory.Replace("'", "'\\''")}' ";

    /// <summary>
    /// <paramref name="line"/> with the prefix immediately before its first claude command word, or null when
    /// it has none (<see cref="FindClaudeCommandWord"/>). Unchanged when <paramref name="configDirectory"/> is
    /// null, which is the default login.
    /// </summary>
    public static string? Apply(string line, string? configDirectory, LaunchShell shell)
    {
        if (configDirectory is null)
        {
            return line;
        }

        return FindClaudeCommandWord(line, shell) is { } index
            ? line.Insert(index, Prefix(configDirectory, shell))
            : null;
    }

    /// <summary>
    /// The window line to print for <paramref name="launchText"/>: prefixed when it is a claude-code text and
    /// the scope is bound, with a notice beside it when a bound claude-code text has no claude command word to
    /// prefix. A text for any other CLI never gets the prefix.
    /// </summary>
    public static PrintedLaunchLine ForWindow(LaunchText launchText, string? configDirectory, LaunchShell shell)
    {
        if (configDirectory is null
            || !string.Equals(LaunchText.NormalizeCli(launchText.Cli), LaunchText.DefaultCli, StringComparison.Ordinal))
        {
            return new PrintedLaunchLine(launchText.Text, null);
        }

        return Apply(launchText.Text, configDirectory, shell) is { } prefixed
            ? new PrintedLaunchLine(prefixed, null)
            : new PrintedLaunchLine(launchText.Text, NoCommandWordNotice(configDirectory));
    }

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
        if (text.Contains(ClaudeConfigDirectory.Variable, StringComparison.OrdinalIgnoreCase))
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
    public static int? FindClaudeCommandWord(string line, LaunchShell shell)
    {
        char escape = shell == LaunchShell.PowerShell
            ? '`'
            : '\\';
        bool commandPosition = true;
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

            switch (current)
            {
                case ' ' or '\t':
                    continue;
                case ';':
                    commandPosition = true;
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
                return index;
            }

            commandPosition = false;
        }

        return null;
    }

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
