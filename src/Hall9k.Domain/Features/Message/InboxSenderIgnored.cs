namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This sender's own outbox commit could not be vouched for by the key in that node's node file
/// (idea 202383dc's sender-verification rule) — every envelope in this sweep was ignored, and
/// nothing from this sender was stored or advanced the cursor. <c>h9k status</c> (M1b) names the
/// sender from this.
/// </summary>
public sealed record InboxSenderIgnored(Guid SenderNodeId, string Reason, DateTimeOffset At);
