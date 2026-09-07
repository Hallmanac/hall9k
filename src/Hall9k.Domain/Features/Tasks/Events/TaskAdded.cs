using System.Text.Json.Serialization;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Model is this task's optional model override, the most specific link in the resolution
/// chain (Decisions Log #33), Unknown when the task states no preference. Appended with a
/// default so streams written before the chain existed replay as Unknown, never as a guess.
/// <para>
/// BlockedBy and StartsAsDraft carry the lifecycle split (Decisions Log #34) with the same
/// discipline. StartsAsDraft defaults to <c>false</c> so a stream written before the split
/// replays exactly as it behaved: added straight into the dispatchable queue and assigned to
/// AddedByOwnerId — which is the sole owner of a v0 install, an observed fact rather than a
/// guess at who a historical task belonged to. Every task h9k creates now passes true.
/// </para>
/// <para>
/// SourceIdeaId is the other half of promotion's two-way provenance (Decisions Log #35): the
/// idea's stream names the task it became, and this names the idea it came from. Null means
/// the task was written directly, which is a fact rather than a gap — and is also how every
/// stream written before ideas existed replays.
/// </para>
/// <para>
/// EpicId is membership, entirely separate from SourceIdeaId's provenance (Decisions Log #100):
/// where a task came from and what it is grouped under are independent records, so a task
/// promoted from an idea and one hand-added with no lineage can sit in the same epic. Null
/// means ungrouped, which is every task's default and unchanged by this field's existence.
/// </para>
/// </summary>
public sealed record TaskAdded(
    Guid Id,
    Guid ProjectId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    TaskType Type,
    string? AgentContext,
    TaskConstraints? Constraints,
    ExternalReference? ExternalReference,
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId,
    AgentModel? Model = null,
    IReadOnlyList<Guid>? BlockedBy = null,
    bool StartsAsDraft = false,
    Guid? SourceIdeaId = null,
    Guid? EpicId = null,
    /// <summary>
    /// This task's own override of which pre-PR review stages a run gets (task: the review
    /// pipeline's stage composition becomes configuration recorded per run); null defers to the
    /// project or node. Recorded normalized (the canonical composition word, never a raw alias) —
    /// see <c>Handlers.TaskDecider.Add</c>. A composition that removes a load-bearing guarantee is
    /// refused by <c>TaskDecider.Add</c> unless <see cref="ReviewStageCompositionAcknowledged"/>
    /// says the consequence was accepted.
    /// </summary>
    ReviewStageComposition? ReviewStageComposition = null,
    /// <summary>Whether removing a load-bearing review guarantee was acknowledged at set time; clamped false when never actually needed.</summary>
    bool ReviewStageCompositionAcknowledged = false,
    /// <summary>
    /// The blocker this task is <em>stacked on</em> rather than merely blocked by (task: a stacked
    /// pull-request edge exists as an explicit opt-in dependency) — always one of
    /// <see cref="BlockedBy"/>, never inferred from it. Null is every task's default and the only
    /// value a stream written before this field existed can replay as, which is exactly right: the
    /// tool never infers stacking from an ordinary blocked-by (Brian's cohesion ruling, 2026-08-28),
    /// so a task that never declared one has none.
    /// </summary>
    Guid? StackedOnTaskId = null,
    /// <summary>
    /// Standing pre-approval declared at creation rather than at publish, as the legacy boolean:
    /// <see cref="PreApprovalMode.LegacyPreApproved"/> — <c>mode == On</c> — recorded beside
    /// <paramref name="PreApproval"/> for exactly the reason
    /// <see cref="TaskPublished.PreApproved"/> is, so a build older than the mode reads a stream
    /// this one wrote without being told after-human-review is a merge it may perform. Read
    /// through <see cref="EffectivePreApproval"/> rather than directly. False on every stream
    /// written before this field existed, which is the honest reading: nobody granted it.
    /// </summary>
    bool PreApproved = false,
    /// <summary>
    /// Which install published the work this task mirrors, when it was adopted from a task record
    /// (see <see cref="Tasks.TaskOrigin"/>). Null on local work, which is almost every task.
    /// </summary>
    TaskOrigin? Origin = null,
    /// <summary>
    /// The three-valued pre-approval granted at creation rather than at publish — the shape
    /// adoption needs (task: a published task's GitHub issue carries the whole task record), since
    /// a task adopted from another install's record arrives as a Draft and the adopting install's
    /// answer to pre-approval has to live somewhere until publish. <c>TaskDecider.Publish</c>
    /// carries it forward, so an ordinary publish never silently drops what was granted here —
    /// changing it is <c>h9k task set-pre-approved</c>'s job. Null on every event written before
    /// the vocabulary existed, which is exactly the case <see cref="PreApprovalMode.Resolve"/>
    /// maps back onto <paramref name="PreApproved"/>.
    /// </summary>
    PreApprovalMode? PreApproval = null,
    /// <summary>
    /// The pull request this task is stacked on when its parent lives on GitHub rather than in this
    /// install's own records (task: a stacked child can stand on a pull request another install
    /// owns) — the other form of the same declaration, and never both at once, which
    /// <see cref="Handlers.TaskDecider.VetStackedEdge"/> refuses. Unlike
    /// <see cref="StackedOnTaskId"/> this implies no <see cref="BlockedBy"/> edge, because there is
    /// no local task to name: what holds the child is the pull request's own observed state
    /// (<see cref="RemoteStackedParentObserved"/>) rather than the unmet-dependency set. Null is
    /// every task's default and the only value a stream written before this field existed can
    /// replay as.
    /// </summary>
    int? StackedOnPullRequestNumber = null)
{
    /// <summary>
    /// What this event granted, whichever build wrote it — the same one home for the
    /// mode-from-boolean mapping that <see cref="TaskPublished.EffectivePreApproval"/> and
    /// <see cref="TaskPreApprovedSet.EffectivePreApproval"/> are. Deliberately not serialized: the
    /// stream carries the facts that were recorded, never a derived one.
    /// </summary>
    [JsonIgnore]
    public PreApprovalMode EffectivePreApproval => PreApprovalMode.Resolve(PreApproval, PreApproved);
}
