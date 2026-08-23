using Hall9k.Domain.Infrastructure.Persistence;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// The environment a service-manager-started daemon needs and would not otherwise
/// have. A daemon started by h9k daemon start inherits the operator's shell; a launchd
/// LaunchAgent starts from launchd's own environment, whose default PATH is
/// /usr/bin:/bin:/usr/sbin:/sbin and nothing else. The daemon resolves its external
/// tools through PATH (ClaudeExecutor spawns claude, GitHubPullRequestInspector and
/// PullRequestOpener spawn gh, the worktree manager spawns git), so a registration
/// that carries no environment makes the two lifecycle paths behave differently even
/// though Decisions Log #31 makes them interchangeable.
///
/// Origin incident (2026-08-19): the pre-PR review of S1-12 found that an autostarted
/// daemon would report running and healthy while every dispatch died — sh exits 127 on
/// an unresolvable claude, RunSupervisor sees a process gone with no result, and the
/// run fails — because the plist carried no EnvironmentVariables.
///
/// Only variables actually set in the enabling shell are recorded; an unset variable
/// stays unset rather than being filled in with a plausible default (AGENTS.md: never
/// guess at unobserved facts). The snapshot is taken at enable time, so moving a tool
/// afterwards means re-running h9k daemon autostart enable.
/// </summary>
public static class DaemonEnvironment
{
    /// <summary>
    /// Everything the daemon and its children read from the environment: PATH for the
    /// external tools, and the HALL9K_* variables that redirect the home directory, the
    /// database, and the agent binary.
    /// </summary>
    private static readonly string[] InheritedVariables =
    [
        "PATH",
        "HALL9K_HOME",
        Hall9kDatabase.EnvironmentVariableName,
        "HALL9K_CLAUDE_PATH",
    ];

    private const string ClaudePathVariable = "HALL9K_CLAUDE_PATH";

    /// <summary>The daemon's external tools, each resolved through PATH unless pinned by a variable.</summary>
    private static readonly string[] ExternalTools = ["gh", "git"];

    /// <summary>The variables this shell actually has, in a stable order for the registration file.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Capture() =>
        [.. InheritedVariables
            .Select(name => new KeyValuePair<string, string?>(name, Environment.GetEnvironmentVariable(name)))
            .Where(variable => !string.IsNullOrEmpty(variable.Value))
            .Select(variable => new KeyValuePair<string, string>(variable.Key, variable.Value!))];

    /// <summary>
    /// The daemon's external tools that the captured environment cannot resolve — the
    /// dispatch-time failure, reported at enable time where the operator can still fix it.
    /// </summary>
    public static IReadOnlyList<string> UnresolvedTools(IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        string[] searchDirectories = [.. Value(environment, "PATH")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        string claude = Value(environment, ClaudePathVariable) is { Length: > 0 } pinned ? pinned : "claude";
        return [.. ExternalTools
            .Prepend(claude)
            .Where(tool => !Resolves(tool, searchDirectories))];
    }

    private static bool Resolves(string tool, IReadOnlyList<string> searchDirectories) => Path.IsPathRooted(tool)
        ? File.Exists(tool)
        : searchDirectories.Any(directory => File.Exists(Path.Combine(directory, tool)));

    private static string Value(IReadOnlyList<KeyValuePair<string, string>> environment, string name) =>
        environment.FirstOrDefault(variable => variable.Key == name).Value ?? string.Empty;
}
