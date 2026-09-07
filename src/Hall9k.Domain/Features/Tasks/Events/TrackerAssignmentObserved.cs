namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The go signal a <c>tracker-assignee</c> claim gate actually saw (idea 64c75e43): at the moment
/// this task's claim was let through, the tracker reported the linked item assigned to this
/// install's own tracker identity. Recorded at every door that passes the gate — the dispatcher's
/// own claim, <c>h9k task work</c>, <c>h9k task start</c>, and <c>h9k task assign</c> — so the
/// stream carries the evidence the claim rested on rather than only the claim.
/// <para>
/// <see cref="AssigneeIdentity"/> is what the tracker's assignee field held, as the tracker
/// spells it: a Jira <c>accountId</c>, or a GitHub login. <see cref="AssigneeName"/> is the
/// human-readable name beside it where the tracker offered one (Jira's <c>displayName</c>), and
/// honestly null where it did not — a GitHub login is the whole of what <c>gh</c> answers, so
/// there is nothing else to record (AGENTS.md, never guess at unobserved facts).
/// <see cref="ObservedAt"/> is this install's own read time and never the tracker's, because the
/// assignee field carries no timestamp of its own: when the assignment was made is not something
/// this read observed.
/// </para>
/// <para>
/// Nothing else about the item is read to produce this, which is what leaves the one-time content
/// snapshot rule intact (Decisions Log #60): the gate asks for the assignee field alone, and the
/// title, body and status this task adopted stay the single snapshot the adoption took.
/// </para>
/// </summary>
public sealed record TrackerAssignmentObserved(
    Guid Id,
    string Reference,
    string AssigneeIdentity,
    string? AssigneeName,
    DateTimeOffset ObservedAt);
