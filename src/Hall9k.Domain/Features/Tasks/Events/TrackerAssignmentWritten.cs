namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// This install took the linked item (idea 64c75e43): the tracker showed it assigned to nobody,
/// <c>h9k task assign --take</c> wrote this install's own tracker identity into its assignee
/// field, and reading the item back afterwards showed that identity holding it. The sibling of
/// <see cref="TrackerAssignmentObserved"/>, and deliberately a different event: that one records a
/// go signal somebody else's act produced, this one records that this install produced it — an
/// audit trail where the two read the same could not answer "who assigned this card" from the
/// stream at all.
/// <para>
/// Every field comes from the read-back rather than from what was asked for, which is the whole
/// reason there is a read-back: a write that reported success and an item that actually carries
/// the assignment are two different facts, and only the second one is worth recording (AGENTS.md,
/// never guess at unobserved facts). <see cref="AssigneeIdentity"/> is what the item's assignee
/// field held when it was read back, as the tracker spells it — a Jira <c>accountId</c>, or a
/// GitHub login — and <see cref="AssigneeName"/> is the human-readable name beside it where the
/// tracker offered one, honestly null where it did not.
/// </para>
/// <para>
/// <see cref="ObservedAt"/> is when this install read the item back, which is the moment the
/// assignment was actually seen; the write itself immediately preceded it. Never the tracker's own
/// time: the assignee field carries none, so when the tenant recorded the change is not something
/// this read observed.
/// </para>
/// <para>
/// A field update and never a transition (Decisions Log #143, #102): the item's status is
/// untouched, so what this records is that the item changed hands, never that it moved through
/// anyone's workflow.
/// </para>
/// </summary>
public sealed record TrackerAssignmentWritten(
    Guid Id,
    string Reference,
    string AssigneeIdentity,
    string? AssigneeName,
    DateTimeOffset ObservedAt);
