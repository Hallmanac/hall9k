namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// A claim-side tracker mirror this node owed but could not confirm landed (idea 202383dc, A3b,
/// criterion 5: "on every holder change the tracker assignee is set to match ... a named outcome
/// retried next sweep") — the identical shape <see cref="TaskHolderReleasePending"/> already gives
/// the ledger holder's own release retry, mirrored for the tracker-assignee write. Deleted the
/// moment a retry lands or the item stops being this node's to mirror (the gate turns off, the
/// reference changes kind, or the item no longer names this install); row keyed by task and node
/// together (<see cref="KeyFor"/>) — two nodes sharing one Postgres must never overwrite each
/// other's pending row for the same task.
/// </summary>
public sealed class TaskTrackerAssignMirrorPending
{
    public string Id { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid NodeId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string LastFailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Consecutive failed attempts this row has recorded, including the one that first wrote it —
    /// what <c>DispatchEngine.SweepPendingTrackerMirrorsAsync</c>'s own backoff schedule reads to
    /// decide whether a retry is due yet, rather than retrying every sweep for as long as this
    /// node holds the task (conformance review, idea 202383dc A3b's fix cycle: a permanently
    /// HeldByOther outcome was otherwise retried with two live gh calls every lease sweep for the
    /// task's whole remaining lifetime).
    /// </summary>
    public int AttemptCount { get; set; }

    public static string KeyFor(Guid taskId, Guid nodeId) => $"{taskId:D}:{nodeId:D}";
}
