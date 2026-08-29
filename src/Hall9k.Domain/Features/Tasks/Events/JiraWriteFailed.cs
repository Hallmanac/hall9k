namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A write attempt that did not land. <see cref="IsAuthFailure"/> is what tells an expired or
/// missing twg login (Brian reports the token expires often enough that re-authentication is
/// routine) apart from every other reason a write can fail: an auth failure keeps the write
/// pending — the same payload succeeds on a later attempt once <c>twg login</c> runs, covered by
/// the daemon's own retry sweep — while any other failure (a bad payload, a Jira validation
/// error, a permission problem) ends it, because retrying the identical request would only fail
/// the identical way; a new one has to be composed.
/// </summary>
public sealed record JiraWriteFailed(
    Guid TaskId,
    Guid WriteId,
    string Reason,
    bool IsAuthFailure,
    DateTimeOffset FailedAt);
