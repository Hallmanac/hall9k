namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Closeout observed that a piece of non-member feedback is gone from GitHub (the review was
/// dismissed or superseded, or the thread was resolved), so its needs-you item no longer asks
/// anything of the owner.
/// </summary>
public sealed record NonMemberFeedbackWithdrawn(Guid Id, string FeedbackKey, DateTimeOffset WithdrawnAt);
