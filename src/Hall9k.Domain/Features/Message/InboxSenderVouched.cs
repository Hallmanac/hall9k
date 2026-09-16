namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This sender's own outbox was vouched for again in <see cref="ProjectId"/> (idea 202383dc, M2's
/// per-project inbox) by a sweep that read successfully but found nothing new past the persisted
/// cursor — so <see cref="InboxCursorAdvanced"/> never fires on its own to clear a prior
/// <see cref="InboxSenderIgnored"/> mark. Carries no seq, unlike a cursor advance: its only fact is
/// "still, or again, vouched", never a new high-water mark, so it never conflicts with
/// <see cref="MessageInboxDecider.AdvanceCursor"/>'s own "a cursor advances to a real seq, at least
/// 1" rule.
/// </summary>
public sealed record InboxSenderVouched(Guid SenderNodeId, Guid ProjectId, DateTimeOffset At);
