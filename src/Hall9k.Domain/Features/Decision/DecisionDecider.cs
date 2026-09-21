using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// Every decision a decision makes, in one place (TASK-MODEL.md §7). There are two rules worth
/// the name. A statement is required, because a decision with no claim in it is a placeholder
/// nobody can cite. And a decision recorded from inside a run is refused unless that run was
/// human-attended: agents record lessons, humans record decisions (idea d805fd8b, Brian's ruling
/// 2026-09-16). The second is deliberately not a permission check on who is typing — the
/// platform cannot see that — it is a check on the one thing it can observe, which is whether
/// the run this call named has a human attached to it.
/// </summary>
public static class DecisionDecider
{
    public static DecisionRecorded Record(
        Guid id,
        KnowledgeScope scope,
        Guid scopeId,
        string statement,
        string? originIncident,
        IReadOnlyList<Guid> supersedes,
        RecordedProvenance provenance,
        DateTimeOffset recordedAt)
    {
        if (statement.IsBlank())
        {
            throw new DomainValidationException(
                "A decision is the claim itself, so there has to be one: h9k decide \"<what was decided>\". "
                + "One claim, stated as a rule rather than a story, and self-contained enough to read on "
                + "its own six months from now.");
        }

        RequireScope(scope, scopeId, "decision");

        if (provenance.IsFromRun && provenance.Attendance != HumanAttendance.Attended)
        {
            throw new DomainValidationException(
                "A decision recorded from inside a run needs a human attending that run, and this run "
                + $"reads {Describe(provenance.Attendance)}. Agents record lessons, humans record "
                + "decisions: h9k learn \"<what this run learned>\" records it as a lesson instead, which "
                + "is live immediately and costs nobody a ruling they never made. A human who wants this "
                + "as a decision records it from their own shell.");
        }

        if (supersedes.Contains(id))
        {
            throw new DomainValidationException("A decision cannot supersede itself.");
        }

        if (supersedes.Distinct().Count() != supersedes.Count)
        {
            throw new DomainValidationException(
                "A decision names each decision it supersedes once — the same id was passed twice.");
        }

        return new DecisionRecorded(
            id, scope, scopeId, statement.Trim(), Trimmed(originIncident), [.. supersedes], provenance, recordedAt);
    }

    /// <summary>
    /// The terminal act, always explicit and always with a reason. <paramref name="supersededBy"/>
    /// is null when nothing replaced this decision — an overruling, or a replacement that has not
    /// been recorded — rather than a guess at the nearest plausible successor.
    /// </summary>
    public static DecisionSuperseded Supersede(
        DecisionAggregate decision,
        Guid? supersededBy,
        string reason,
        Guid supersededByOwnerId,
        DateTimeOffset supersededAt)
    {
        if (decision.Status == DecisionStatus.Superseded)
        {
            throw new DomainConflictException(
                $"Decision {decision.Id} was already superseded"
                + (decision.SupersededAt is { } when ? $" on {when:u}" : string.Empty)
                + ". Nothing is deleted here, so the record stands as it is; record the newer ruling as its "
                + "own decision instead: h9k decide \"<the ruling that holds now>\".");
        }

        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                "Superseding a decision needs a reason — what changed, or what was wrong with it: "
                + $"h9k decide supersede {decision.Id} --reason \"<why it stopped binding>\".");
        }

        if (supersededBy == decision.Id)
        {
            throw new DomainValidationException("A decision cannot supersede itself.");
        }

        return new DecisionSuperseded(decision.Id, supersededBy, reason.Trim(), supersededByOwnerId, supersededAt);
    }

    /// <summary>Shared with <see cref="Learning.LearningDecider"/>'s own identical check, spelled out once per slice rather than shared across two tiny flat slices.</summary>
    private static void RequireScope(KnowledgeScope scope, Guid scopeId, string noun)
    {
        if (scope != KnowledgeScope.Project && scope != KnowledgeScope.Owner)
        {
            throw new DomainValidationException(
                $"A {noun} is scoped to a project or to an owner, and '{scope}' is neither.");
        }

        if (scopeId == Guid.Empty)
        {
            throw new DomainValidationException(
                $"A {noun} scoped to a {scope.Value.ToLowerInvariant()} needs that "
                + $"{scope.Value.ToLowerInvariant()}'s own id — an empty one records nothing anybody can read back.");
        }
    }

    private static string? Trimmed(string? value) => value.IsNotBlank() ? value.Trim() : null;

    private static string Describe(HumanAttendance attendance) =>
        attendance == HumanAttendance.Unattended
            ? "unattended (a dispatched agent, a deliberate headless start, or a delegated contractor)"
            : "with no attendance observed either way";
}
