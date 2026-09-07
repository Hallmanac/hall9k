using System.Text.Json.Serialization;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Draft -> Published: the readiness gate passed (Decisions Log #34). Publishing promises
/// two things about the state it produces — the task satisfies the readiness contract, and
/// a human may assign it at any moment — which is why validation and cycle detection live
/// here alone and revision stops here.
/// </summary>
/// <param name="NoExistingItemAttested">
/// True when a project tracking its backlog (Jira or GitHub issues) required proof no
/// existing item already covers this task, and the publisher supplied it via
/// <c>--no-existing-item</c> instead of a link (backlog: publishing an untracked task under a
/// tracking backlog policy). <see cref="PublishedAt"/> and <see cref="PublishedByOwnerId"/> are
/// the attestation's own who and when — it is made in the same breath as the publish it gates.
/// False for a policy of none, false whenever the task already carried a reference, and false
/// whenever a publication was already pending (<c>h9k task push-to-jira</c>, run while still a
/// Draft) — none of those cases ever asks for one, so the flag is clamped to false rather than
/// recorded verbatim from the caller.
/// </param>
/// <param name="UntrackedAttested">
/// The sibling attestation to <see cref="NoExistingItemAttested"/>, same shape, opposite
/// choice: the publisher supplied <c>--untracked</c> to deliberately skip external tracking for
/// this task rather than searching the tracker and either linking a match or confirming none
/// exists. <see cref="PublishedAt"/> and <see cref="PublishedByOwnerId"/> are this attestation's
/// who and when too. Unlike <see cref="NoExistingItemAttested"/>, only one of the gate's
/// never-asked states clamps this flag to false silently — an already-linked task, where
/// nothing would be created either way. The other two never reach this event at all: a policy
/// of none (or one this build's closed set doesn't recognize) and a publication already pending
/// both refuse the publish outright rather than clamp, because a deliberate opt-out nobody
/// asked for, or one that would silently override an in-flight publication, is worth teaching
/// about rather than swallowing.
/// </param>
/// <param name="PreApproved">
/// True when the publisher gave standing pre-approval at publish time (task: a task can be
/// published pre-approved): the owner stops being a synchronous gate at the pull request, and the
/// daemon merges it on its own once GitHub's own gates are satisfied. Defaults false — an
/// unflagged task's behaviour is entirely unchanged. Flippable afterward on any live non-terminal
/// task via <c>h9k task set-pre-approved</c> (<see cref="TaskPreApprovedSet"/>) without the
/// unassign-draft-revise-publish ceremony <see cref="TaskRevised"/> would otherwise require.
/// <para>
/// This is the legacy boolean now that pre-approval is three-valued: it is
/// <see cref="PreApprovalMode.LegacyPreApproved"/> — <c>mode == On</c> — recorded so a build older
/// than <paramref name="PreApproval"/> reads a stream this one wrote. After-human-review therefore
/// reads as not pre-approved on such a build, which is the fail-closed direction and the whole
/// reason the mapping is <c>== On</c> rather than <c>!= Off</c>: that build knows nothing of the
/// human-review gate, so <c>true</c> would invite it to merge straight past one the owner asked
/// for. Read through <see cref="EffectivePreApproval"/> rather than directly — on a stream written
/// before the mode existed it is the only record of what the publisher chose.
/// </para>
/// </param>
/// <param name="PreApproval">
/// The three-valued mode the publisher actually gave (task: the people a pull request is waiting
/// on are named, and pre-approval gains a mode that waits for human review) — off, on, or
/// after-human-review. Null on every event written before the vocabulary existed, which is exactly
/// the case <see cref="PreApprovalMode.Resolve"/> maps back onto <paramref name="PreApproved"/>.
/// </param>
public sealed record TaskPublished(
    Guid Id,
    DateTimeOffset PublishedAt,
    Guid PublishedByOwnerId,
    bool NoExistingItemAttested = false,
    bool UntrackedAttested = false,
    bool PreApproved = false,
    PreApprovalMode? PreApproval = null,
    /// <summary>
    /// This task's own override of whether true closeout closes its linked GitHub issue (task: a
    /// task's linked GitHub issue is closed at true closeout under a configurable rule);
    /// present-with-null clears the override so the project's own close-linked-issue setting
    /// decides again, live — the same present-with-null-clears idiom
    /// <see cref="TaskRevised.ReviewStageComposition"/> already uses. Absent leaves whatever an
    /// earlier <see cref="TaskRevised"/> already recorded on this still-Draft task untouched.
    /// </summary>
    Optional<CloseLinkedIssueRule?> CloseLinkedIssue = default)
{
    /// <summary>
    /// What this event means, whichever build wrote it — the one home for the mode-from-boolean
    /// mapping, shared with <see cref="TaskPreApprovedSet.EffectivePreApproval"/>. Deliberately not
    /// serialized: the stream carries the facts that were recorded, never a derived one.
    /// </summary>
    [JsonIgnore]
    public PreApprovalMode EffectivePreApproval => PreApprovalMode.Resolve(PreApproval, PreApproved);
}
