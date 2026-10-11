namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The owner declined one piece of non-member feedback (<c>h9k pr feedback --decline</c>). Nothing
/// dispatches for it. A declined review stops holding a pre-approved merge; a declined thread stays
/// unresolved on GitHub and goes on holding the merge, exactly as any unresolved thread does.
/// </summary>
public sealed record NonMemberFeedbackDeclined(
    Guid Id, string FeedbackKey, DateTimeOffset DeclinedAt, Guid DeclinedByOwnerId);
