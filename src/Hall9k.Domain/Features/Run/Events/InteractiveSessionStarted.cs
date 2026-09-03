namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// h9k task work launched an interactive Claude Code session attached to the operator's
/// terminal. ClaudeSessionId is the id the CLI minted (a fresh claim) or the previously recorded
/// id it resumed (`claude --resume`, a re-entry — PLAN.md §16 #124, amending #103's original
/// "always fresh" choice), so it is a precise pointer rather than something scraped from a
/// transcript the platform never reads (interactive sessions are not headless — there is no
/// stream-json to parse). A run can carry more than one of these: closing the terminal leaves the
/// task claimed with no liveness lease (AGENTS.md), and re-running `h9k task work` on the same
/// task resumes the most recently recorded ClaudeSessionId — the same id repeats across this run's
/// attach/detach cycles when resume succeeds — falling back to a fresh ClaudeSessionId, recorded
/// here exactly the same way, only when that id cannot be resumed (no matching local
/// conversation).
/// <para>
/// ProcessId is the operating-system process the CLI just spawned it as — recorded only once the
/// process is actually alive, so this event is never appended for a launch that never started
/// (the claude binary missing, the worktree vanishing). Paired with <see cref="StartedAt"/>, it
/// is a process identity (the pid-reuse guard, Decisions Log #2) another command in another
/// terminal can ask the operating system about, so it can refuse to act on a worktree this
/// session is still attached to rather than silently colliding with it (adversarial review,
/// cycle 1).
/// </para>
/// <para>
/// MachineName is <see cref="Environment.MachineName"/> at the moment the process was spawned —
/// the only place an interactive claim's machine identity is ever recorded, since its
/// <c>RunDispatched</c> deliberately carries the <see cref="Guid.Empty"/> node sentinel. Without
/// it, a reader on a different machine sharing the same database has no way to tell that a
/// recorded pid names a process in a process table it cannot see, and would answer about a
/// stranger (adversarial review, cycle 2). Blank on a stream written before this field existed.
/// </para>
/// </summary>
public sealed record InteractiveSessionStarted(
    Guid Id, Guid ClaudeSessionId, DateTimeOffset StartedAt, int ProcessId, string MachineName = "",
    string SessionName = "");
