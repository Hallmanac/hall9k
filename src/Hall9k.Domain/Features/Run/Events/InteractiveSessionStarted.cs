namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// h9k task work launched an interactive Claude Code session attached to the operator's
/// terminal. ClaudeSessionId is the id the CLI minted and handed to `claude --session-id`, so
/// it is a precise pointer rather than something scraped from a transcript the platform never
/// reads (interactive sessions are not headless — there is no stream-json to parse). A run can
/// carry more than one of these: closing the terminal leaves the task claimed with no liveness
/// lease (AGENTS.md), and re-running `h9k task work` on the same task re-enters the same
/// worktree and branch with a fresh session, which is a fresh ClaudeSessionId on this same run.
/// <para>
/// ProcessId is the operating-system process the CLI just spawned it as — recorded only once the
/// process is actually alive, so this event is never appended for a launch that never started
/// (the claude binary missing, the worktree vanishing). Paired with <see cref="StartedAt"/>, it
/// is a process identity (the pid-reuse guard, Decisions Log #2) another command in another
/// terminal can ask the operating system about, so it can refuse to act on a worktree this
/// session is still attached to rather than silently colliding with it (adversarial review,
/// cycle 1).
/// </para>
/// </summary>
public sealed record InteractiveSessionStarted(Guid Id, Guid ClaudeSessionId, DateTimeOffset StartedAt, int ProcessId);
