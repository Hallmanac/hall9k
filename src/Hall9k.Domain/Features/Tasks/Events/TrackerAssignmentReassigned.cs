namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// An operator at a terminal confirmed taking the linked item from the people the tracker showed
/// holding it, this install wrote its own tracker identity over them, and reading the item back
/// afterwards showed that identity holding it alone. The sibling of
/// <see cref="TrackerAssignmentWritten"/>, and deliberately a different event that takes its place
/// for this write rather than joining it: a stream carrying both would read as two writes, and one
/// that spelt them the same could not answer "who was put off this card" at all.
/// <para>
/// <see cref="ReplacedHolders"/> is every person the fresh read showed on the item immediately
/// before the write, as the tracker spelt them: an identity (a Jira <c>accountId</c>, a Linear user
/// id, or a GitHub login) and a display name where the tracker offered one, never an email.
/// <see cref="AssigneeIdentity"/> and <see cref="AssigneeName"/> are what the read-back showed
/// holding the item, so they come from the observation and not from what was asked for.
/// <see cref="ObservedAt"/> is when this install read the item back.
/// </para>
/// <para>
/// It carries third parties' names, which no other task-stream record does, and it is
/// project-scoped, so it travels signed and not encrypted to every member of the project. That is
/// the point of it: a reassignment takes an item off other people, so the record has to say who.
/// </para>
/// </summary>
public sealed record TrackerAssignmentReassigned(
    Guid Id,
    string Reference,
    IReadOnlyList<ReplacedTrackerAssignee> ReplacedHolders,
    string AssigneeIdentity,
    string? AssigneeName,
    DateTimeOffset ObservedAt);
