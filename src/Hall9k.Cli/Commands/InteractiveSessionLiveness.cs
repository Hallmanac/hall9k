using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Whether an interactive claim's own attached Claude Code process is still alive somewhere —
/// duplicated in miniature from Hall9k.Daemon.ProcessManagement.ProcessManagerBase's own
/// pid-plus-start-time identity check (Decisions Log #2), rather than referenced, because the CLI
/// cannot reference the daemon project (Reference graph: Cli -> Domain + Connectors).
/// <para>
/// Without this, h9k task verify/deliver/handback could not tell an operator's own attached
/// session — running right now in a different terminal — from a worktree nobody is touching, and
/// would run gates, push, or redispatch a headless agent into it regardless, racing whatever the
/// attached session is doing (adversarial review, cycle 1).
/// </para>
/// </summary>
internal static class InteractiveSessionLiveness
{
    // Mirrors ProcessManagerBase.StartTimeTolerance: start times can drift slightly between
    // recording and reading, and a match within this window means "same process", not a pid the
    // OS already recycled for something else.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Refuses with the reason an operator can act on when this run's own interactive session is
    /// still attached; a no-op otherwise (no interactive session was ever recorded, or the one
    /// that was has already ended or exited without a matching InteractiveSessionEnded — closing
    /// the terminal is a normal way to leave, so an operator who did that is not blocked here).
    /// </summary>
    public static void EnsureNotAttachedElsewhere(RunDetails run, Guid taskId, string action)
    {
        if (run.ActiveSessions.Find(session => session.Role == AgentRole.Interactive) is not { } session
            || session.StartedAt is not { } startedAt
            || !IsAlive(session.ProcessId, startedAt))
        {
            return;
        }

        throw new DomainConflictException(
            $"Task {taskId}'s interactive session (pid {session.ProcessId}) is still attached in another "
            + $"terminal — exit it first (Ctrl+D or /exit) before you {action} from here.");
    }

    private static bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            DateTimeOffset actualStartedAt = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (actualStartedAt - startedAt).Duration() <= StartTimeTolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // ArgumentException: no process with that id exists any more. InvalidOperationException
            // / Win32Exception: the pid now belongs to another (often privileged) process whose
            // start time this process cannot read — nothing the operator's session spawned.
            return false;
        }
    }
}
