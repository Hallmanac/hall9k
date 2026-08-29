namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// h9k task work launched an interactive Claude Code session attached to the operator's
/// terminal. ClaudeSessionId is the id the CLI minted and handed to `claude --session-id`, so
/// it is a precise pointer rather than something scraped from a transcript the platform never
/// reads (interactive sessions are not headless — there is no stream-json to parse). A run can
/// carry more than one of these: closing the terminal leaves the task claimed with no liveness
/// lease (AGENTS.md), and re-running `h9k task work` on the same task re-enters the same
/// worktree and branch with a fresh session, which is a fresh ClaudeSessionId on this same run.
/// </summary>
public sealed record InteractiveSessionStarted(Guid Id, Guid ClaudeSessionId, DateTimeOffset StartedAt);
