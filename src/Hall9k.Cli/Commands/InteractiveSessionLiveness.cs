using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console;

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
    /// <summary>
    /// Carries the claiming run's id into the launched Claude Code process's own environment
    /// (<c>TaskWorkCommand.LaunchInteractiveClaudeAsync</c>), inherited by every descendant it
    /// spawns. A nested <c>h9k task verify</c> invoked from inside that very session reads it back
    /// to recognise itself — the one case <see cref="EnsureNotAttachedElsewhere"/> cannot
    /// otherwise tell apart from a second terminal, since that session is blocked waiting on the
    /// command it just started rather than racing it (conformance review, cycle 2). Checked by
    /// callers for whom self-invocation is actually safe rather than built into this guard itself:
    /// <c>h9k task work</c>'s own re-entry spawns a second, concurrent session rather than
    /// blocking on this one, so the same exemption there would open the collision this guard
    /// exists to prevent.
    /// </summary>
    public const string InteractiveRunEnvironmentVariable = "HALL9K_INTERACTIVE_RUN_ID";

    /// <summary>
    /// Set by Claude Code itself — not this platform — to the running `claude` process's own OS
    /// process id, inherited by every Bash-tool child it spawns. Verified empirically against a
    /// live Claude Code CLI session's own environment (2026-09-03), the same "verified rather than
    /// assumed from memory" discipline <c>ClaudeSettingsFile</c>'s own doc comment already holds
    /// its two Bash-tool env vars to. <see cref="TaskRegisterSessionCommand"/> reads it as the
    /// process identity a directly-launched session got for free from <c>Process.Start()</c>'s own
    /// return value; <see cref="IsSelfInvocation"/> reads it to recognise a self-registered
    /// session asking this guard about itself, the counterpart of
    /// <see cref="InteractiveRunEnvironmentVariable"/> for a session hall9k never spawned and so
    /// never had the chance to inject that variable into.
    /// </summary>
    public const string ClaudeCodePidEnvironmentVariable = "CLAUDE_PID";

    // Mirrors ProcessManagerBase.StartTimeTolerance: start times can drift slightly between
    // recording and reading, and a match within this window means "same process", not a pid the
    // OS already recycled for something else.
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Refuses with the reason an operator can act on when this run's own interactive session is
    /// still attached; a no-op otherwise (no interactive session was ever recorded, or the one
    /// that was has already ended or exited without a matching InteractiveSessionEnded — closing
    /// the terminal is a normal way to leave, so an operator who did that is not blocked here).
    /// <paramref name="force"/> is the operator's own attestation, after checking by hand, that a
    /// session recorded on another machine has actually exited — it only ever bypasses that
    /// unobservable cross-machine case; a session this machine can itself confirm is still alive
    /// always refuses, force or not. <paramref name="quiet"/> suppresses only the cross-machine
    /// --force notice below, for a caller that already showed this exact notice for this same run
    /// earlier in the same command and would otherwise print it twice (h9k task work's own
    /// pre-launch re-check, right after ReenterAsync's identical check — conformance review,
    /// cycle 6). The return value tells such a caller whether the notice fired, so it can pass
    /// <paramref name="quiet"/> on to its own repeat check.
    /// </summary>
    public static bool EnsureNotAttachedElsewhere(RunDetails run, Guid taskId, string action, bool force = false, bool quiet = false)
    {
        if (run.ActiveSessions.Find(session => session.Role == AgentRole.Interactive) is not { } session
            || session.StartedAt is not { } startedAt)
        {
            return false;
        }

        // A blank MachineName (a stream written before the field existed) is an unknown machine —
        // nothing was ever observed either way, so this proceeds exactly as an absent session
        // would. A non-blank name that does not match this machine IS an observation (a session
        // recorded somewhere), and this machine cannot read that machine's process table to tell
        // whether it is still alive — unobservable is not evidence of "gone", so this refuses
        // rather than silently proceeding (adversarial review, cycle 4; the earlier cycle-2
        // reading collapsed both cases into "proceed", which let a second machine race an
        // operator's still-attached session on the first one). --force is the human attestation
        // lever every other unobservable-fact refusal in this codebase pairs a refusal with
        // (h9k task resolve --reason, h9k task publish --no-existing-item): without it, a claim
        // whose recorded session names a machine that is lost, reimaged, or simply not to hand
        // could never be released, handed back, delivered, or re-entered from anywhere else
        // (adversarial review, cycle 1).
        if (session.MachineName.IsNotBlank() && session.MachineName != Environment.MachineName)
        {
            if (force)
            {
                if (!quiet)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[yellow]Task {taskId}'s interactive session (pid {session.ProcessId}) was recorded on machine '{session.MachineName}' and cannot be checked from here — proceeding on --force because you confirmed it has exited.[/]");
                }

                return true;
            }

            throw new DomainConflictException(
                $"Task {taskId}'s interactive session (pid {session.ProcessId}) was recorded on machine "
                + $"'{session.MachineName}' — this machine ('{Environment.MachineName}') cannot check whether "
                + $"it is still attached there. Confirm on {session.MachineName} that the session has exited, "
                + $"then re-run with --force to {action} from here.");
        }

        if (session.MachineName.IsBlank() || !IsAlive(session.ProcessId, startedAt))
        {
            return false;
        }

        // A session this machine can itself confirm is alive is an observed fact, not an
        // unobservable one, and force never overrides it. The one case worth naming specially is
        // self-invocation: the attached session running this very command against itself, which
        // reads as "still attached" exactly like a second terminal would, but there is no second
        // terminal to exit (conformance review, cycle 1).
        bool selfInvocation = IsSelfInvocation(run);
        throw new DomainConflictException(selfInvocation
            ? $"Task {taskId}'s interactive session (pid {session.ProcessId}) is this very session — you cannot "
              + $"{action} from inside it. Exit it first (Ctrl+D or /exit), then {action} from your own terminal."
            : $"Task {taskId}'s interactive session (pid {session.ProcessId}) is still attached in another "
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

    /// <summary>
    /// Whether the caller invoking this very command IS this run's own recorded interactive
    /// session, rather than a second one — the case <see cref="EnsureNotAttachedElsewhere"/>'s own
    /// alive branch names specially, and the pre-check <c>h9k task verify</c> already uses to skip
    /// that guard entirely for the one caller safe to exempt (it is blocked waiting on the command
    /// it just started, not racing it).
    /// <para>
    /// Two independent signals, either sufficient on its own: <see cref="InteractiveRunEnvironmentVariable"/>
    /// is the direct-launch path's own — set on the child's environment at <c>Process.Start()</c>
    /// time (<c>TaskWorkCommand.LaunchInteractiveClaudeAsync</c>), inherited by every descendant.
    /// A self-registered session was never spawned by this platform, so that variable was never
    /// injected into it; for that path, <see cref="ClaudeCodePidEnvironmentVariable"/> — read
    /// straight from Claude Code's own environment, not something hall9k set — matching this run's
    /// recorded <see cref="ActiveSession.ProcessId"/> is the equivalent observation: it is
    /// literally the same OS process asking about itself. Checked only once the caller already
    /// knows a live session is recorded on this machine (this method never claims one exists on
    /// its own), so a coincidental CLAUDE_PID present in an unrelated shell can never manufacture a
    /// false match against a run this session was never part of.
    /// </para>
    /// <para>
    /// A bare pid match is not enough on its own, unlike <see cref="InteractiveRunEnvironmentVariable"/>'s
    /// run-id comparison: a pid is a small, OS-recycled integer, so a registered session that
    /// exited without ever calling deliver/handback/release (leaving its now-stale
    /// <see cref="ActiveSession"/> entry on record — nothing here appends an ended event the way a
    /// direct launch's own exit does) could have its old pid reassigned by the OS to a genuinely
    /// unrelated later process, which would then wrongly read as this very session if only the pid
    /// were compared. <see cref="IsAlive"/>'s own start-time tolerance check — the exact guard
    /// pid-reuse already has everywhere else in this file — rules that out: a coincidentally
    /// recycled pid almost never started at the recorded instant too.
    /// </para>
    /// </summary>
    public static bool IsSelfInvocation(RunDetails run)
    {
        if (Environment.GetEnvironmentVariable(InteractiveRunEnvironmentVariable) == run.Id.ToString())
        {
            return true;
        }

        if (run.ActiveSessions.Find(session => session.Role == AgentRole.Interactive) is not { StartedAt: { } startedAt } interactive)
        {
            return false;
        }

        string? claudePid = Environment.GetEnvironmentVariable(ClaudeCodePidEnvironmentVariable);
        return claudePid.IsNotBlank() && int.TryParse(claudePid, out int pid) && pid == interactive.ProcessId
            && IsAlive(pid, startedAt);
    }

    /// <summary>
    /// Mirrors <c>Hall9k.Daemon.ProcessManagement.ProcessManagerBase.ReadStartedAt</c> exactly, and
    /// shared by <c>TaskWorkCommand</c> (a launch it just performed) and
    /// <see cref="TaskRegisterSessionCommand"/> (a process it is only just learning about) rather
    /// than duplicated a second time in the same project: <see cref="DateTimeOffset.MinValue"/> is
    /// recorded instead of a plausible-looking guess when the process's own start time cannot be
    /// read — AGENTS.md's "never guess at unobserved facts" — which guarantees no later liveness
    /// check ever matches a real process's start time against it.
    /// </summary>
    public static DateTimeOffset ReadStartedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
