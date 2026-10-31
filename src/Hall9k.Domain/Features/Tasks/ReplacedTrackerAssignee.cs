namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// One person a tracker reassignment replaced, exactly as the tracker showed them: its own identity for
/// them and the name beside it when it gave one, never an email. A value carried inside
/// <see cref="Events.TrackerAssignmentReassigned"/> rather than an event of its own, which is why it
/// sits beside the aggregate and not in the events folder.
/// </summary>
public sealed record ReplacedTrackerAssignee(string Identity, string? Name);
