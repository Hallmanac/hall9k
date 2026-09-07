using Hall9k.Domain.Features.Orchestrator;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The default <c>claude-code</c> launch line (task: an operator starts a lean node or project
/// orchestrator window) — what <c>h9k orchestrator launch-text show</c> prints before anybody has
/// ever run <c>launch-text set</c>. Rendered, not stored, until a human or the
/// <c>orchestrator-recipe-generator</c> skill records a real setting with <c>launch-text set</c>;
/// this is the seed that command starts from.
/// <para>
/// Every flag here is load-bearing (Decisions Log, the 2026-09-05 held-message incident): a wrong
/// one is exactly how a session ends up holding a message it should have accepted, or inheriting
/// context it was launched lean specifically to avoid. A dedicated test fails outright if any of
/// them drops.
/// </para>
/// </summary>
public static class LaunchTextDefaults
{
    /// <summary>Relative to the working directory the launch line already <c>cd</c>s into.</summary>
    public const string AnchorRelativePath = "recipes/launch-anchor.md";

    /// <summary>Relative to the working directory the launch line already <c>cd</c>s into.</summary>
    public const string SettingsRelativePath = "recipes/settings.json";

    /// <summary>
    /// Only <see cref="LaunchText.DefaultCli"/> has a computed default today (Decisions Log: one
    /// setting per CLI, and only Claude Code is discovered yet); any other name has nothing to
    /// synthesize, so this returns null rather than guessing at a command line for a runtime the
    /// platform has never launched.
    /// </summary>
    public static LaunchText? For(string cli, string workingDirectory, string openingMessage)
    {
        if (!string.Equals(LaunchText.NormalizeCli(cli), LaunchText.DefaultCli, StringComparison.Ordinal))
        {
            return null;
        }

        string command = Render(workingDirectory, openingMessage);
        return new LaunchText(LaunchText.DefaultCli, command);
    }

    /// <summary>
    /// Rendered per the host's own operating system (design context: Windows users run h9k in
    /// PowerShell 7, so the node's launch line has to be a PowerShell line) — <c>;</c> chains a
    /// PowerShell command the way <c>&amp;&amp;</c> chains a POSIX one, and this always renders for
    /// whichever OS is actually running the command that asked, never a fixed platform choice.
    /// </summary>
    public static string Render(string workingDirectory, string openingMessage)
    {
        string separator = OperatingSystem.IsWindows() ? ";" : " &&";
        return $"cd \"{workingDirectory}\"{separator} claude "
            + "--strict-mcp-config "
            + "--setting-sources project "
            + $"--settings {SettingsRelativePath} "
            + "--dangerously-skip-permissions "
            + $"--append-system-prompt-file {AnchorRelativePath} "
            + $"\"{openingMessage}\"";
    }
}
