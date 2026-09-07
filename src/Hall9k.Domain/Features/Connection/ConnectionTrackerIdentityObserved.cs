namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// Who this connection's credentials actually are, as the tracker itself answered — the identity
/// a project's <c>tracker-assignee</c> claim gate compares an item's assignee against (idea
/// 64c75e43). For Jira that is the <c>accountId</c> <c>/rest/api/2/myself</c> returns, which is
/// the only thing Jira's own assignee field carries: an email address or a display name would be
/// matching on something a tenant is free to change under us, and neither is what the field
/// holds.
/// <para>
/// Recorded as its own observation rather than folded into
/// <see cref="ConnectionRegistered"/>/<see cref="ConnectionReregistered"/>, the same
/// observed-vs-platform-act separation <c>WorkItemLinked</c> already draws, because the two
/// happen at different moments: <c>h9k connection add jira</c> already asks
/// <c>/rest/api/2/myself</c> to prove the credentials work, so it appends this in the same
/// breath — while a connection registered before this event existed carries no identity at all,
/// and the first gate check that needs one reads it live and appends this then. Either way the
/// stream says what was observed and when, and nothing is ever typed or inferred.
/// </para>
/// <para>
/// GitHub has no counterpart here on purpose. <c>gh</c> carries the machine's own login, read
/// fresh per check through <c>GitHubReviewAssignments.CurrentLoginAsync</c> and deliberately
/// never cached: a recorded login would silently stop matching (or start matching someone else's
/// assignments) the moment the machine's <c>gh auth</c> session changed.
/// </para>
/// </summary>
public sealed record ConnectionTrackerIdentityObserved(
    Guid Id,
    string TrackerAccountId,
    DateTimeOffset ObservedAt);
