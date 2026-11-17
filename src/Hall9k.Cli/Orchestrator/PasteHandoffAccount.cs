using Hall9k.Domain.Features.Connection;
using Spectre.Console;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// What a paste handoff (<c>h9k task drive --print-prompt</c>, <c>h9k pr review</c>) prints for a project bound to
/// a Claude Code account: a line naming the account and a start command that runs under it. The person starts the
/// session themselves, so the account has to ride on the command they are handed. An unbound project gets neither.
/// </summary>
public static class PasteHandoffAccount
{
    /// <summary>
    /// The start command for <paramref name="account"/> on <paramref name="settingsFile"/>, or null for the default
    /// login, whose handoff prints none.
    /// </summary>
    public static string? StartLine(ClaudeCodeAccount account, string settingsFile, LaunchShell shell) =>
        account.ConfigDirectory is { } directory
            ? LaunchTextDefaults.HandoffStartLine(settingsFile, directory, shell)
            : null;

    /// <summary>Names <paramref name="account"/>, then writes <paramref name="startLine"/> raw on a line of its own.</summary>
    public static void Write(ClaudeCodeAccount account, string startLine)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]This project runs under its bound Claude Code account, {account.Describe()}. Start your session with the command below, which sets that account for it and passes the settings file:[/]");
        LaunchLineWriter.Write(startLine);
    }
}
