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
    Optional<Guid?> EpicId = default);
