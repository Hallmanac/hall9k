using Hall9k.Domain.Infrastructure.Ids;
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
    /// A lesson merged out of others, which must cite them (idea d805fd8b, piece 5; backlog 55).
    /// The citation is the whole point of the entry point existing: distillation is the one act in
    /// this slice that produces a claim no single run earned, so a distilled lesson that cites
    /// nothing is indistinguishable from an agent inventing doctrine and calling it a merge. Every
    /// rule <see cref="Record"/> holds still holds here, and this adds three:
    /// <list type="bullet">
    /// <item>at least one source, so the merge is checkable against what it merged;</item>
    /// <item>no source repeated, since a citation list is a set and a repeat is a mistake rather
    /// than emphasis;</item>
    /// <item>no source that is this lesson itself, which would make the claim its own evidence.</item>
    /// </list>
    /// <para>
    /// What this cannot do, and does not pretend to: nothing forces a session that merged two
    /// lessons to come through here rather than through <see cref="Record"/> with a freshly typed
    /// claim. The platform has no way to read intent off a sentence. So the guarantee is narrower
    /// than "every merged lesson cites its sources" and exactly this: a lesson that CLAIMS to be a
    /// distillation carries citations somebody can check, or it does not exist. The distillation
    /// task's own instructions are what ask for the claim; this is what makes the claim mean
    /// something.
    /// </para>
    /// </summary>
    public static LearningRecorded RecordDistilled(
        Guid id,
        KnowledgeScope scope,
        Guid scopeId,
        string statement,
        IReadOnlyList<Guid> distilledFrom,
        RecordedProvenance provenance,
        DateTimeOffset recordedAt)
    {
        if (distilledFrom.Count == 0)
        {
            throw new DomainValidationException(
                "A distilled lesson has to cite the lessons it was merged out of: h9k learn "
                + "\"<the merged claim>\" --distilled-from <id> --distilled-from <id>. Without the "
                + "citations nobody reading it later can check the merge against what it merged, and "
                + "a merge nobody can check is a new claim wearing a merge's clothes. If this really "
                + "is something this run learned on its own rather than a merge, record it as one: "
                + "h9k learn \"<what you learned>\".");
        }

        if (distilledFrom.Contains(id))
        {
            throw new DomainValidationException(
                $"A distilled lesson cannot cite itself ({DomainId.Short(id)}) as a source. Cite the "
                + "lessons it merges; h9k learn list shows them.");
        }

        Guid[] repeated = [.. distilledFrom.GroupBy(source => source)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];
        if (repeated.Length > 0)
        {
            throw new DomainValidationException(
                "A distilled lesson's sources are a set, so each one is cited once: "
                + string.Join(", ", repeated.Select(DomainId.Short))
                + (repeated.Length == 1 ? " appears" : " appear") + " more than once.");
        }

        if (distilledFrom.Contains(Guid.Empty))
        {
            throw new DomainValidationException(
                "An empty id is not a lesson this cites: pass the id of each lesson being merged, "
                + "which h9k learn list shows.");
        }

        LearningRecorded recorded = Record(id, scope, scopeId, statement, provenance, recordedAt);
        return recorded with { DistilledFrom = [.. distilledFrom] };
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
