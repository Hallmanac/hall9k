namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This node's own cursor for one sender advanced to <see cref="Seq"/> — the highest seq a sweep
/// actually looked at, whether or not it was stored. Never the ledger's own concern (idea
/// 202383dc: "cursors are local"), only this node's own store.
/// </summary>
public sealed record InboxCursorAdvanced(Guid SenderNodeId, long Seq, DateTimeOffset At);
