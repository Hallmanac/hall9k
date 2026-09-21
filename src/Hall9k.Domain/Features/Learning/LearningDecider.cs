using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// Every decision a lesson makes, in one place (TASK-MODEL.md §7). There is deliberately no
/// attendance gate here, unlike <see cref="Decision.DecisionDecider"/>'s: recording a lesson is
/// exactly what a dispatched agent is meant to do the moment it learns something, and a lesson
/// primed before it was corroborated costs one line of a prompt (IDEA-learning-capture, "Why
/// there is no quarantine"). What the lesson does carry is its provenance, so a reader can see
/// whether an unattended run or a human wrote it.
/// </summary>
public static class LearningDecider
{
    public static LearningRecorded Record(
        Guid id,
        KnowledgeScope scope,
        Guid scopeId,
        string statement,
        RecordedProvenance provenance,
        DateTimeOffset recordedAt)
    {
        if (statement.IsBlank())
        {
            throw new DomainValidationException(
                "A lesson is the claim itself, so there has to be one: h9k learn \"<what you learned>\". "
                + "One claim, phrased as an instruction to the next agent, and self-contained: no run ids, "
                + "no file paths from this worktree, nothing that only makes sense inside this session.");
        }

        RequireScope(scope, scopeId);

        return new LearningRecorded(id, scope, scopeId, statement.Trim(), provenance, recordedAt);
    }

    /// <summary>
    /// The terminal act, always explicit and always with a reason. Nothing retires a lesson on
    /// age or on absence of reinforcement: a lesson that works suppresses its own evidence, so
    /// silence is never evidence of death (IDEA-learning-capture, "Staleness").
    /// </summary>
    public static LearningRetired Retire(
        LearningAggregate learning, string reason, Guid retiredByOwnerId, DateTimeOffset retiredAt)
    {
        if (learning.Status == LearningStatus.Retired)
        {
            throw new DomainConflictException(
                $"Lesson {learning.Id} was already retired"
                + (learning.RetiredAt is { } when ? $" on {when:u}" : string.Empty)
                + ". Nothing is deleted here, so the record stands as it is.");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                "Retiring a lesson needs a reason — wrong, absorbed into something better, or graduated "
                + $"into a rule that is now enforced somewhere harder: h9k learn retire {learning.Id} "
                + "--reason \"<why it stopped earning its line>\".");
        }

        return new LearningRetired(learning.Id, reason.Trim(), retiredByOwnerId, retiredAt);
    }

    private static void RequireScope(KnowledgeScope scope, Guid scopeId)
    {
        if (scope != KnowledgeScope.Project && scope != KnowledgeScope.Owner)
        {
            throw new DomainValidationException(
                $"A lesson is scoped to a project or to an owner, and '{scope}' is neither.");
        }

        if (scopeId == Guid.Empty)
        {
            throw new DomainValidationException(
                $"A lesson scoped to a {scope.Value.ToLowerInvariant()} needs that "
                + $"{scope.Value.ToLowerInvariant()}'s own id — an empty one records nothing anybody can read back.");
        }
    }
}
