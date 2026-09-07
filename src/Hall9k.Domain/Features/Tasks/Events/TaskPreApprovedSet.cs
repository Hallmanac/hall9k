using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human flips this task's standing pre-approval after publish (task: a task can be published
/// pre-approved) — deliberately state-agnostic in the same sense
/// <see cref="TaskSessionCapOverridden"/> is, but with one guard that override does not carry:
/// refused on Abandoned, and on a Done task whose pull request has already merged — closeout
/// observed it — since neither has a future pull request left for the flag to govern. A Done task
/// whose pull request is still open is not refused: closeout has not yet observed a merge, so
/// pre-approval still has something left to govern. <see cref="TaskDecider.SetPreApproved"/> is
/// the only place that guard is enforced.
/// </summary>
/// <param name="PreApproved">
/// The legacy boolean, still recorded so a build older than <paramref name="PreApproval"/> reads a
/// stream this one wrote: it is <see cref="PreApprovalMode.LegacyPreApproved"/> — <c>mode == On</c>,
/// the only mode such a build can carry out — so after-human-review reads there as not pre-approved
/// at all rather than as a merge that build would perform without the gate. Read through
/// <see cref="EffectivePreApproval"/> rather than directly: on a stream written before the mode
/// existed this is the only record of what the owner chose.
/// </param>
/// <param name="PreApproval">
/// The three-valued mode the owner actually set (task: the people a pull request is waiting on are
/// named, and pre-approval gains a mode that waits for human review). Null on every event written
/// before the vocabulary existed, which is exactly the case
/// <see cref="PreApprovalMode.Resolve"/> maps back onto <paramref name="PreApproved"/>.
/// </param>
public sealed record TaskPreApprovedSet(
    Guid Id,
    bool PreApproved,
    DateTimeOffset SetAt,
    Guid SetByOwnerId,
    PreApprovalMode? PreApproval = null)
{
    /// <summary>
    /// What this event means, whichever build wrote it — the one home for the mode-from-boolean
    /// mapping, so no reader repeats it. Deliberately not serialized: the stream carries the two
    /// facts that were recorded, never a third derived from them.
    /// </summary>
    [JsonIgnore]
    public PreApprovalMode EffectivePreApproval => PreApprovalMode.Resolve(PreApproval, PreApproved);
}
