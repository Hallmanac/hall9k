using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A revision of the task's own text and dependency edges, permitted only in Draft
/// (Decisions Log #34). Every field is <see cref="Optional{T}"/> with the same meaning it
/// carries on ProjectSettingsChanged: absent means "left alone", present means "this is the
/// new value" — so a revision that only rewords the objective records only that, and the
/// stream never claims the criteria were retyped identically.
/// </summary>
public sealed record TaskRevised(
    Guid Id,
    Optional<string> Objective,
    Optional<IReadOnlyList<string>> AcceptanceCriteria,
    Optional<string> AgentContext,
    Optional<IReadOnlyList<Guid>> BlockedBy,
    Optional<TaskType> Type,
    Optional<AgentModel> Model,
    DateTimeOffset RevisedAt,
    Guid RevisedByOwnerId,
    /// <summary>
    /// Absent leaves the epic alone; present with a value joins that epic, present with null
    /// leaves it (Decisions Log #100: a task joins or leaves at add or revise, no
    /// ceremony beyond the ordinary Draft-only revision gate).
    /// </summary>
    Optional<Guid?> EpicId = default,
    /// <summary>
    /// Absent leaves the queue-first marker alone; present sets or clears it (task 45136b29,
    /// idea fcaded0b's R7 ruling). Unlike every other field here, this one is the single
    /// exception <see cref="Handlers.TaskDecider.Revise"/>'s own gate carves out of Draft-only:
    /// a scheduling fact, not part of the readiness contract, so it is settable on a task that
    /// has already left Draft as long as nothing else in the same revision travels with it.
    /// </summary>
    Optional<bool> QueuePriority = default,
    /// <summary>
    /// This task's own override of which pre-PR review stages a run gets (task: the review
    /// pipeline's stage composition becomes configuration recorded per run); present-with-null
    /// clears the override so the project or node decides again. A composition that removes a
    /// load-bearing guarantee is refused by <c>Handlers.TaskDecider.Revise</c> unless
    /// <see cref="ReviewStageCompositionAcknowledged"/> says the consequence was accepted.
    /// </summary>
    Optional<ReviewStageComposition?> ReviewStageComposition = default,
    /// <summary>Whether removing a load-bearing review guarantee was acknowledged at set time; clamped false when never actually needed.</summary>
    bool ReviewStageCompositionAcknowledged = false,
    /// <summary>
    /// True clears <see cref="TaskAggregate.InteractiveModeEnabled"/> directly (task: interactive
    /// mode becomes a recorded property of the task). The one other exception
    /// <see cref="Handlers.TaskDecider.Revise"/>'s own gate carves out of Draft-only, alongside
    /// <see cref="QueuePriority"/>: the two ordinary exit doors, <c>h9k task handback</c> and
    /// <c>h9k task release</c>, both need an active interactive claim to act on, and a headless
    /// follow-up dispatched under a real node claim while the flag is still on — or a task that
    /// has already reached Done with its pull request open — leaves neither door reachable.
    /// False never turns the flag on; only a claim carrying <c>TaskClaimed.InteractiveMode</c> does that.
    /// </summary>
    bool ClearInteractiveMode = false,
    /// <summary>
    /// Absent leaves the stacked edge alone; present with a value declares this task stacked on
    /// that blocker, present with null drops the edge back to a plain blocked-by (task: a stacked
    /// pull-request edge exists as an explicit opt-in dependency). Draft-only like every other
    /// contract field here, and never inferred: dropping the edge leaves the blocked-by dependency
    /// itself untouched, because those are two separate declarations.
    /// </summary>
    Optional<Guid?> StackedOnTaskId = default,
    /// <summary>
    /// <see cref="StackedOnTaskId"/>'s remote twin: absent leaves the edge alone, present with a
    /// value declares this task stacked on that pull request on GitHub, present with null drops the
    /// edge (task: a stacked child can stand on a pull request another install owns). Draft-only
    /// like its twin, and mutually exclusive with it — both enforced by
    /// <see cref="Handlers.TaskDecider.Revise"/>.
    /// </summary>
    Optional<int?> StackedOnPullRequestNumber = default,
    /// <summary>
    /// This task's own override of whether true closeout closes its linked GitHub issue (task: a
    /// task's linked GitHub issue is closed at true closeout under a configurable rule);
    /// present-with-null clears the override so the project's own close-linked-issue setting
    /// decides again, live, exactly as <see cref="ReviewStageComposition"/>'s own clearing idiom
    /// works for its setting.
    /// </summary>
    Optional<CloseLinkedIssueRule?> CloseLinkedIssue = default,
    /// <summary>
    /// A spike's own kind, absent leaves it alone (task: a spike is a run, not a walk). One of the
    /// three fields <c>Handlers.TaskDecider.Revise</c> lets through on a Published spike, alongside
    /// <see cref="ExitCriterion"/> and <see cref="Constraints"/> — every other field here stays
    /// Draft-only.
    /// </summary>
    Optional<SpikeKind> SpikeKind = default,
    /// <summary>A spike's own exit criterion, absent leaves it alone. Settable on a Published spike, the same carve-out <see cref="SpikeKind"/> gets.</summary>
    Optional<string> ExitCriterion = default,
    /// <summary>A spike's own budget, absent leaves it alone, present-with-null clears it. Settable on a Published spike, the same carve-out <see cref="SpikeKind"/> gets.</summary>
    Optional<TaskConstraints?> Constraints = default,
    /// <summary>
    /// This task's own reasoning effort, the most specific link in the effort chain: it wins over the
    /// project's value and the node's, on every node, because it travels with the task like
    /// <see cref="Model"/> does. Absent leaves it alone; present with
    /// <see cref="AgentEffort.Unknown"/> clears it so the levels beneath decide again. Draft-only like
    /// <see cref="Model"/>.
    /// </summary>
    Optional<AgentEffort> Effort = default,
    /// <summary>
    /// Absent leaves the human gate alone; present sets it or clears it
    /// (<see cref="TaskAggregate.HumanGate"/>). A scheduling-style fact, not part of the readiness
    /// contract, so <see cref="Handlers.TaskDecider.Revise"/> carves it out of Draft-only the way it
    /// does <see cref="QueuePriority"/>: settable on any live task whose pull request has not opened.
    /// </summary>
    Optional<bool> HumanGate = default,
    /// <summary>
    /// The plan's refinement session this revision is the result of, set only by the revise the daemon runs for a session
    /// that has ended (<see cref="RefinementSessionEnvironmentVariable"/>). It is how the draft's record tells a session's
    /// own revision from a person's made while the session ran: the first leaves the "revised while it ran" facts alone,
    /// the second sets them. Null for every revision a person makes.
    /// </summary>
    Guid? RefinementSessionId = null)
{
    /// <summary>
    /// Read by <c>h9k task revise</c>: the id of the refinement session whose revision it is applying. When set, the revise
    /// is applied only to the draft that session read (nobody revised it since the session was dispatched) and is recorded
    /// under a version fence, so the check and the append cannot be separated by a person's own revise.
    /// </summary>
    public const string RefinementSessionEnvironmentVariable = "HALL9K_PLAN_REFINEMENT_SESSION";

    /// <summary>
    /// Whether this revision touched something a draft challenge reads and judges: the objective, the acceptance
    /// criteria, the agent context, or one of the recorded facts the prompt lists beside them (type, dependencies,
    /// stacking, epic). A verdict can hold a must about any of those, and revising one is how that must gets
    /// applied. A revision of anything the prompt does not carry (effort, model, queue marker, review stage
    /// composition, and the like) leaves a verdict's changes exactly as unapplied as they were, so it does not
    /// count as having revised the draft since a verdict. A method and not a property, so it is never serialized
    /// with the event.
    /// </summary>
    public bool ChangesWhatChallengeReads() =>
        Objective.HasValue
        || AcceptanceCriteria.HasValue
        || AgentContext.HasValue
        || Type.HasValue
        || BlockedBy.HasValue
        || EpicId.HasValue
        || StackedOnTaskId.HasValue
        || StackedOnPullRequestNumber.HasValue;
}
