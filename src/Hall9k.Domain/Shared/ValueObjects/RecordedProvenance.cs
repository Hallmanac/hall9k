namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Where a recorded decision or lesson came from (idea d805fd8b, piece 1), observed rather than
/// inferred: the owner whose install appended it, the run and task it was recorded from if any,
/// and whether a human was attending that run. Every field here is something the platform itself
/// read — the node and the owner root fingerprint are not repeated, because
/// <c>EventOriginStampingListener</c> already stamps both onto every event's own metadata
/// (idea 202383dc).
/// <para>
/// <see cref="RunId"/> and <see cref="TaskId"/> are explicitly null for a lesson typed at a shell:
/// nothing named a run, so nothing is claimed about one. They are never filled in from a working
/// directory or a branch name, which would be a guess dressed as an observation (AGENTS.md,
/// "never guess at unobserved facts").
/// </para>
/// </summary>
public sealed record RecordedProvenance(
    Guid RecordedByOwnerId,
    Guid? RunId,
    Guid? TaskId,
    HumanAttendance Attendance)
{
    /// <summary>The shell call's own provenance: an owner, and honest nulls for everything a run would have supplied.</summary>
    public static RecordedProvenance FromShell(Guid ownerId) =>
        new(ownerId, RunId: null, TaskId: null, HumanAttendance.Unobserved);

    /// <summary>True when this was recorded from inside a run, which is what <c>DecisionDecider</c>'s own attendance gate turns on.</summary>
    public bool IsFromRun => RunId is not null;
}
