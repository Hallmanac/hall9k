namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// A ledger holder release this node owed but could not confirm landed (idea 202383dc, A3b) — the
/// domain side already gave the task back (<c>TaskHolderReleased</c> is on its stream), but the
/// conditional write clearing <c>TaskRecord.Holder</c> failed (a fetch, a push, a signing, or a
/// read failure) and needs a later sweep to retry rather than being lost. Deleted the moment a
/// retry lands; row keyed by task and node together (<see cref="KeyFor"/>) — two nodes sharing one
/// Postgres must never overwrite each other's pending row for the same task.
/// </summary>
public sealed class TaskHolderReleasePending
{
    /// <summary>Task and node together (<see cref="KeyFor"/>) — the identical reason <c>TrackerClaimHold</c>'s own key carries both: two nodes sharing one Postgres must never overwrite each other's pending row for the same task.</summary>
    public string Id { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid NodeId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string LastFailureReason { get; set; } = string.Empty;

    public static string KeyFor(Guid taskId, Guid nodeId) => $"{taskId:D}:{nodeId:D}";
}
