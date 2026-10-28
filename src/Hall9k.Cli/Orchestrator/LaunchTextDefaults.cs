using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Shared.ValueObjects;

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
    public static string Render(string workingDirectory, string openingMessage) =>
        Render(workingDirectory, openingMessage, HostShell);

    /// <summary>The shell the host's own operating system runs a pasted line in.</summary>
    public static LaunchShell HostShell => OperatingSystem.IsWindows()
        ? LaunchShell.PowerShell
        : LaunchShell.Posix;

    /// <summary><see cref="Render(string, string)"/> for an explicit target shell, so one host can render both.</summary>
    public static string Render(string workingDirectory, string openingMessage, LaunchShell shell) =>
        $"cd \"{EscapeForDoubleQuotes(workingDirectory, shell)}\"{Separator(shell)} claude "
        + "--strict-mcp-config "
        + "--setting-sources project "
        + $"--settings {SettingsRelativePath} "
        + "--dangerously-skip-permissions "
        + $"--append-system-prompt-file {AnchorRelativePath} "
        + $"\"{EscapeForDoubleQuotes(openingMessage, shell)}\"";

    /// <summary>
    /// The line <c>h9k task drive</c> prints for a human's own session on one task: <c>cd</c> into the
    /// worktree, then <c>claude</c> under the arguments <c>--direct-launch</c> passes
    /// (<c>TaskDriveCommand.BuildInteractiveArguments</c>: the run's settings file, the claim's session
    /// name, skip-permissions only when the project allows it), plus the task anchor and the model and
    /// effort a headless build of the task would get. It deliberately omits the orchestrator line's
    /// <c>--strict-mcp-config</c> and <c>--setting-sources project</c>: a task session is the owner's
    /// own and keeps their user-scope settings. A resumed conversation is addressed by its session id in
    /// place of the opening message, and the fresh line rides along as the fallback for a conversation
    /// that cannot be found.
    /// </summary>
    public static TaskDriveLaunchText RenderTaskDrive(TaskDriveLaunch launch, LaunchShell shell)
    {
        string fresh = TaskDriveLine(launch, shell, resume: false);
        string? effortNotice = launch.Effort.IsWellFormed
            ? null
            : NoEffortNotice;
        return launch.ResumeSessionId is not null
            ? new TaskDriveLaunchText(TaskDriveLine(launch, shell, resume: true), fresh, effortNotice)
            : new TaskDriveLaunchText(fresh, null, effortNotice);
    }

    /// <summary>Said beside a task line that names no <c>--effort</c>, because nothing sets one for this build.</summary>
    public const string NoEffortNotice =
        "No reasoning effort is set anywhere for this task's build, so the line names none and the model's own default applies, as it would to a headless run.";

    private static string TaskDriveLine(TaskDriveLaunch launch, LaunchShell shell, bool resume)
    {
        string Quoted(string value) => $"\"{EscapeForDoubleQuotes(value, shell)}\"";
        string resumeArgument = resume && launch.ResumeSessionId is { } sessionId
            ? $"--resume {sessionId} "
            : string.Empty;
        string skipPermissions = launch.SkipPermissions
            ? "--dangerously-skip-permissions "
            : string.Empty;
        string effort = launch.Effort.IsWellFormed
            ? $" --effort {launch.Effort.Value}"
            : string.Empty;
        string openingMessage = resume
            ? string.Empty
            : $" {Quoted(launch.OpeningMessage)}";
        return $"cd {Quoted(launch.WorktreePath)}{Separator(shell)} claude {resumeArgument}"
            + $"--name {Quoted(launch.SessionName)} "
            + $"--settings {Quoted(launch.SettingsFile)} "
            + skipPermissions
            + $"--append-system-prompt-file {Quoted(launch.AnchorFile)} "
            + $"--model {Quoted(launch.Model.Value)}{effort}{openingMessage}";
    }

    private static string Separator(LaunchShell shell) => shell == LaunchShell.PowerShell
        ? ";"
        : " &&";

    /// <summary>
    /// Escapes <paramref name="value"/> for the double-quoted segment it is about to sit inside,
    /// per the host shell this line is rendered for: a project name (<c>ProjectDecider.Register</c>
    /// only rejects blank, never shell metacharacters) or a home path can carry a <c>"</c>,
    /// backtick, or <c>$</c> that would otherwise close the quoted segment early or trigger command
    /// substitution the moment an operator pastes this line — the non-adversarial shape of the
    /// same defect is a bare <c>"</c> simply breaking the line (independent pre-PR review, cycle 3,
    /// adversarial lens). PowerShell's own escape character is the backtick, not backslash.
    /// </summary>
    private static string EscapeForDoubleQuotes(string value, LaunchShell shell) => shell == LaunchShell.PowerShell
        ? value.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"")
        : value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
}

/// <summary>The shell a launch line is written for.</summary>
public enum LaunchShell
{
    Posix,
    PowerShell,
}

/// <summary>What <see cref="LaunchTextDefaults.RenderTaskDrive"/> renders a task line from.</summary>
/// <param name="ResumeSessionId">The conversation to resume, or null for a fresh one.</param>
public sealed record TaskDriveLaunch(
    string WorktreePath, string AnchorFile, string SettingsFile, string SessionName, string OpeningMessage,
    AgentModel Model, AgentEffort Effort, bool SkipPermissions, Guid? ResumeSessionId);

/// <param name="Line">The line to paste: the resume line when a conversation is being resumed, otherwise the fresh one.</param>
/// <param name="FreshFallbackLine">The fresh line, present only beneath a resume line.</param>
/// <param name="EffortNotice">One sentence to print beside the line when it names no effort.</param>
public sealed record TaskDriveLaunchText(string Line, string? FreshFallbackLine, string? EffortNotice);
