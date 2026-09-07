using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Run.Documents;

/// <summary>
/// A clean-base comparison's own conclusive verdict for one gate against one commit of the
/// project's base branch, remembered per node so a red main is diagnosed once rather than
/// re-attempted and abandoned on every failing run against it (task: the clean-base comparison
/// can actually finish — origin incident 2026-09-05/06, roughly thirteen hours of a red main
/// where every one of five failed runs paid for its own fresh comparison against the same broken
/// commit because nothing remembered the previous one's answer). Mutable telemetry, NOT an event
/// (Decisions Log #7's own convention for <c>TaskLease</c>/<c>RunActivity</c>): this is a cache of
/// an observation, not a fact worth an immutable history of its own.
/// <para>
/// The commit sha is part of <see cref="ComputeId"/>, so a later comparison against a base branch
/// that has since moved never touches this row at all — it writes a new one under the new commit's
/// own key instead (adversarial review, cycle 1, low: an earlier version of this comment claimed
/// the base branch moving past a commit causes "a later comparison at the same key" to overwrite
/// it, which cannot happen once the key includes the commit). A row here is only ever overwritten
/// by a second write at the identical key, and <c>DescribeCleanBaseComparisonAsync</c> returns from
/// the cache before ever reaching that write when a matching key already exists, so in practice a
/// row is written once and never revisited. Nothing here reclaims it: one row accumulates per
/// (node, project, gate, command, failing base commit) with no retention path, and
/// <see cref="RecordedAt"/> is recorded but read by nothing in the solution — a future reader
/// wanting an expiry will need to add one, not assume this comment already promises it.
/// </para>
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

    /// <summary>
    /// Keyed on the gate's own <paramref name="gateCommand"/> as well as its name and base commit
    /// (independent pre-PR review, cycle 1, both lenses, medium): a project's base commit sha does
    /// not move when an operator only edits <c>h9k project set --verify</c>, so a key on name and
    /// commit alone would replay a verdict recorded for the gate's PREVIOUS command as an
    /// observation of whatever command it was just changed to — the exact "these are still the
    /// gates that ran" question <see cref="Hall9k.Domain.Features.Project.VerifyCommand.Fingerprint"/>
    /// exists to answer for a run's own pass/fail decision, just unasked here. Hashed rather than embedded verbatim
    /// so an arbitrarily long or delimiter-carrying command (a `--filter` expression, embedded
    /// quotes) cannot blow out the id's own length or collide with the surrounding fields the way
    /// a bare join could.
    /// </summary>
    public static string ComputeId(Guid nodeId, Guid projectId, string gate, string gateCommand, string baseCommitSha) =>
        $"{nodeId:N}-{projectId:N}-{gate}-{ComputeCommandFingerprint(gateCommand)}-{baseCommitSha}";

    private static string ComputeCommandFingerprint(string gateCommand) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gateCommand)))[..16];
}
