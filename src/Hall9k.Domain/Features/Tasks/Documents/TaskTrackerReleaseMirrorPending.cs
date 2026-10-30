namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// The release-side twin of <see cref="TaskTrackerAssignMirrorPending"/>: a tracker-assignee clear
/// this node owed on giving back the ledger holder, but could not confirm landed (idea 202383dc,
/// A3b, criterion 5). Written by a give-back only (h9k task abandon, h9k task release, and lease
/// expiry on a project with the claim gate off), never by a completion: a finished item keeps its
/// assignee, so the sweep drops a row whose task is Done without calling the tracker. Deleted the
/// moment a retry lands, the gate turns off, the item no longer shows this install holding it, or
/// the task is Done (so a failed clear from h9k task release on a Done task is not retried); row
/// keyed by task and node together (<see cref="KeyFor"/>), the identical reason
/// <see cref="TaskHolderReleasePending"/>'s own key carries both.
/// </summary>
public sealed class TaskTrackerReleaseMirrorPending
{
    public string Id { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid NodeId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string LastFailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Consecutive failed attempts this row has recorded, including the one that first wrote it —
    /// the release-side twin of <see cref="TaskTrackerAssignMirrorPending.AttemptCount"/>, read by
    /// the identical backoff schedule in <c>DispatchEngine.SweepPendingTrackerMirrorsAsync</c>.
    /// </summary>
    public int AttemptCount { get; set; }

    public static string KeyFor(Guid taskId, Guid nodeId) => $"{taskId:D}:{nodeId:D}";
}
