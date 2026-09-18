namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// The release-side twin of <see cref="TaskTrackerAssignMirrorPending"/>: a tracker-assignee clear
/// this node owed on giving back the ledger holder, but could not confirm landed (idea 202383dc,
/// A3b, criterion 5). Deleted the moment a retry lands, the gate turns off, or the item no longer
/// shows this install holding it; row keyed by task and node together (<see cref="KeyFor"/>), the
/// identical reason <see cref="TaskHolderReleasePending"/>'s own key carries both.
/// </summary>
public sealed class TaskTrackerReleaseMirrorPending
{
    public string Id { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid NodeId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string LastFailureReason { get; set; } = string.Empty;

    public static string KeyFor(Guid taskId, Guid nodeId) => $"{taskId:D}:{nodeId:D}";
}
