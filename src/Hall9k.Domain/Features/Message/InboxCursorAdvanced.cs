namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This node's own cursor for one sender, scoped to <see cref="ProjectId"/> (idea 202383dc, M2's
/// per-project inbox), advanced to <see cref="Seq"/> — the highest seq a sweep actually looked at,
/// whether or not it was stored. Never the ledger's own concern (idea 202383dc: "cursors are
/// local"), only this node's own store, and never shared across projects: reading this same
/// sender's outbox through a different project never touches this cursor.
/// </summary>
public sealed record InboxCursorAdvanced(Guid SenderNodeId, Guid ProjectId, long Seq, DateTimeOffset At);
