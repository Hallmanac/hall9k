namespace Hall9k.Domain.Features.Run.Documents;

/// <summary>
/// A clean-base comparison's own conclusive verdict for one gate against one commit of the
/// project's base branch, remembered per node so a red main is diagnosed once rather than
/// re-attempted and abandoned on every failing run against it (task: the clean-base comparison
/// can actually finish — origin incident 2026-09-05/06, roughly thirteen hours of a red main
/// where every one of five failed runs paid for its own fresh comparison against the same broken
/// commit because nothing remembered the previous one's answer). Mutable telemetry, NOT an event
/// (Decisions Log #7's own convention for <c>TaskLease</c>/<c>RunActivity</c>): this is a cache of
/// an observation, not a fact worth an immutable history of its own, and a later comparison at the
/// same key simply overwrites it as the base branch moves past the commit this verdict was
/// recorded against.
/// <para>
/// Keyed by node rather than shared platform-wide (<see cref="ComputeId"/>): a clean checkout's
/// own reachability and state is a per-node fact, mirroring
/// <see cref="Queries.GateDurationHistoryQuery"/>'s own per-node duration history for the
/// identical reason. Only ever written for a checkout confirmed clean at the recorded commit — an
/// unconfirmed checkout's own local state can differ next time even at the identical commit sha,
/// so a verdict observed there is never safe to remember or reuse.
/// </para>
/// <para>
/// An <c>Inconclusive</c> attempt is never written here at all (AGENTS.md's
/// own "never guess at unobserved facts": an attempt that could not even answer the question has
/// nothing honest to remember), so a document existing at a given key always means the gate was
/// actually observed to pass or fail there.
/// </para>
/// </summary>
public sealed class CleanBaseGateVerdict
{
    public string Id { get; set; } = string.Empty;
    public Guid NodeId { get; set; }
    public Guid ProjectId { get; set; }
    public string Gate { get; set; } = string.Empty;
    public string BaseCommitSha { get; set; } = string.Empty;
    public bool BasePasses { get; set; }
    /// <summary>The comparison note to reuse verbatim when <see cref="BasePasses"/> is false; null when it is true.</summary>
    public string? FailureNote { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    public static string ComputeId(Guid nodeId, Guid projectId, string gate, string baseCommitSha) =>
        $"{nodeId:N}-{projectId:N}-{gate}-{baseCommitSha}";
}
