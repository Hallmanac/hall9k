using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Whether a person is the one answering at this process's terminal, as opposed to a console that
/// merely exists. A terminal alone is not evidence: a dispatched session, a detached contractor and an
/// orchestrator's Bash tool can all hold a pty or an inherited stdin, and an act that must never be
/// taken by an agent, such as moving a tracker item off another person, cannot rest on that.
/// <para>
/// So attended means two things together: every standard stream is a terminal, and none of the
/// markers hall9k and Claude Code stamp on the sessions they spawn is present. Another vendor's CLI
/// whose shell tool holds a pty is not caught until that vendor has an environment marker of its own.
/// </para>
/// </summary>
internal static class AttendedSession
{
    /// <summary>The variables whose presence means this process is a session some other process started, never a person's own shell.</summary>
    internal static readonly IReadOnlyList<string> UnattendedMarkers =
    [
        DispatchedRunEnvironment.RunIdVariable,
        HeadlessLaunch.DetachedSessionEnvironmentVariable,
        InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable,
    ];

    /// <summary>
    /// Read through an injected reader, not <see cref="Environment.GetEnvironmentVariable(string)"/>,
    /// because the environment is process-wide and shared across parallel test classes.
    /// </summary>
    /// <param name="interactive">Whether stdin, stdout and stderr are all terminals (<c>Capabilities.Interactive</c>).</param>
    /// <param name="environmentVariable">Reads one environment variable, null when it is unset.</param>
    public static bool IsAttended(bool interactive, Func<string, string?> environmentVariable) =>
        interactive && UnattendedMarkers.All(name => environmentVariable(name).IsBlank());
}
