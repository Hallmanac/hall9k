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
    bool ClearInteractiveMode = false);
