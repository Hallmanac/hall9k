namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// Why this node's last claim attempt could not take the ledger holder lock (idea 202383dc, A3b)
/// — mutable telemetry, not an event, the same standing <see cref="TrackerClaimHold"/> gives the
/// tracker-assignee gate and for the identical reason: a stand-down is a measurement the next
/// sweep overwrites, never a fact about the task's own history. <c>Id == <see cref="KeyFor"/>(TaskId, NodeId)</c>.
/// <para>
/// Two distinct causes share this one row, told apart by whether <see cref="HolderNodeId"/> is
/// set: a genuine <c>HeldByOther</c> stand-down names the other node and since — the same "held
/// by X since Y" moment a replicated claim's own HeldElsewhere rendering states, though the two
/// are read off different sources and name the holder differently (this row's <see cref="HolderNodeName"/>
/// comes from the ledger record's own holder block; HeldElsewhere's own rendering names a prefix
/// of <c>TaskClaimed.OwnerRootFingerprint</c> off the replicated domain event) — they never
/// actually disagree in practice only because the two sentences never render on the same row, one
/// requiring <c>TaskState.Queued</c> and the other a claimed one; a fail-closed hold (a fetch,
/// push, signing, or read failure) names no holder at all, only <see cref="Cause"/>, because
/// nothing was actually read.
/// </para>
/// </summary>
public sealed class TaskHolderClaimHold
{
    public string Id { get; set; } = string.Empty;

    public Guid TaskId { get; set; }

    /// <summary>The node whose sweep observed this — the machine an operator would look at.</summary>
    public Guid NodeId { get; set; }

    public string MachineName { get; set; } = string.Empty;

    /// <summary>Set only for a genuine HeldByOther stand-down; null for a fail-closed hold, which observed nothing.</summary>
    public Guid? HolderNodeId { get; set; }

    public string? HolderOwnerFingerprint { get; set; }

    public string? HolderNodeName { get; set; }

    public DateTimeOffset? HolderSince { get; set; }

    /// <summary>Why a fail-closed hold could not even read the ledger; null for a genuine HeldByOther stand-down.</summary>
    public string? Cause { get; set; }

    public DateTimeOffset ObservedAt { get; set; }

    public static string KeyFor(Guid taskId, Guid nodeId) => $"{taskId:D}:{nodeId:D}";
}
