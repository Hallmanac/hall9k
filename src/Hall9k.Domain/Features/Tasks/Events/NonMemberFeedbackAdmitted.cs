namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The owner gave the go for one piece of non-member feedback (<c>h9k pr feedback --go</c>). The
/// next closeout poll reads it like a member's: it dispatches the one lap that carries it, and
/// names it to the session as owner-approved. Replicated as an owner-only act, since it is the one
/// event that lets a stranger's text reach a session that skips permissions.
/// </summary>
public sealed record NonMemberFeedbackAdmitted(
    Guid Id, string FeedbackKey, DateTimeOffset AdmittedAt, Guid AdmittedByOwnerId);
