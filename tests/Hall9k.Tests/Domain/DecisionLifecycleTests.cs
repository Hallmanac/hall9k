using FluentAssertions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A binding decision as event-sourced platform data (idea d805fd8b, piece 1): recorded with a
/// stable citation key and observed provenance, superseded only by an explicit act carrying a
/// reason, and refused outright when an unattended run tries to mint one.
/// </summary>
public sealed class DecisionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    [Fact]
    public void Recording_keeps_the_claim_the_scope_and_the_incident_behind_it()
    {
        Guid id = DomainId.New();
        DecisionRecorded recorded = DecisionDecider.Record(
            id, KnowledgeScope.Project, Project,
            "  Agents never push; the daemon pushes every branch with --force-with-lease  ",
            "2026-08-17, PR #6: a plain push stranded two rebased follow-up branches",
            [], RecordedProvenance.FromShell(Owner), Now);

        DecisionAggregate decision = new();
        decision.Apply(recorded);

        decision.Id.Should().Be(id, "the id is the citation key, minted at recording");
        decision.Statement.Should().Be(
            "Agents never push; the daemon pushes every branch with --force-with-lease");
        decision.Scope.Should().Be(KnowledgeScope.Project);
        decision.ScopeId.Should().Be(Project);
        decision.OriginIncident.Should().StartWith("2026-08-17");
        decision.Status.Should().Be(DecisionStatus.Recorded);
        decision.SupersededByDecisionId.Should().BeNull();
    }

    /// <summary>
    /// The never-guess rule at the field level: a decision typed at a shell observed no run and
    /// no task, and the event says exactly that rather than filling in a plausible pair.
    /// </summary>
    [Fact]
    public void A_statement_typed_at_a_shell_carries_explicit_nulls_for_run_and_task()
    {
        DecisionRecorded recorded = DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "One claim", originIncident: null,
            [], RecordedProvenance.FromShell(Owner), Now);

        recorded.Provenance.RunId.Should().BeNull();
        recorded.Provenance.TaskId.Should().BeNull();
        recorded.Provenance.RecordedByOwnerId.Should().Be(Owner);
        recorded.Provenance.Attendance.Should().Be(
            HumanAttendance.Unobserved, "nothing was observed about who is at the keyboard");
        recorded.Provenance.IsFromRun.Should().BeFalse();
        recorded.OriginIncident.Should().BeNull("no incident was recorded, and none is invented");
    }

    [Fact]
    public void An_unattended_run_is_refused_and_told_to_record_a_lesson_instead()
    {
        RecordedProvenance fromAnAgent = new(
            Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unattended);

        Action act = () => DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "Agents decide things now",
            originIncident: null, [], fromAnAgent, Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*h9k learn*", "the refusal has to name the door that is open")
            .WithMessage("*unattended*");
    }

    /// <summary>
    /// A run whose session was never named records nothing this platform can read attendance off,
    /// and an unread fact is not evidence of a human — so the gate fails closed on it too, not
    /// only on a positively unattended run.
    /// </summary>
    [Fact]
    public void A_run_with_no_observed_attendance_is_refused_the_same_way()
    {
        RecordedProvenance unobservedRun = new(
            Owner, DomainId.New(), DomainId.New(), HumanAttendance.Unobserved);

        Action act = () => DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "A ruling from nowhere",
            originIncident: null, [], unobservedRun, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*h9k learn*");
    }

    [Fact]
    public void An_attended_run_records_the_decision_with_that_runs_own_provenance()
    {
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();
        RecordedProvenance attended = new(Owner, runId, taskId, HumanAttendance.Attended);

        DecisionRecorded recorded = DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "Recorded from an operator's own claim",
            originIncident: null, [], attended, Now);

        recorded.Provenance.RunId.Should().Be(runId);
        recorded.Provenance.TaskId.Should().Be(taskId);
        recorded.Provenance.Attendance.Should().Be(HumanAttendance.Attended);
    }

    [Fact]
    public void An_owner_scoped_decision_is_the_cross_project_habit_and_carries_the_owner_as_its_scope_id()
    {
        DecisionRecorded recorded = DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Owner, Owner, "Split ternaries across lines",
            originIncident: null, [], RecordedProvenance.FromShell(Owner), Now);

        recorded.Scope.Should().Be(KnowledgeScope.Owner);
        recorded.ScopeId.Should().Be(Owner);
    }

    [Fact]
    public void Recording_refuses_an_empty_claim_and_says_what_a_good_one_looks_like()
    {
        Action act = () => DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "   ", originIncident: null,
            [], RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*h9k decide*")
            .WithMessage("*One claim*");
    }

    [Fact]
    public void Recording_refuses_a_scope_with_no_id_behind_it()
    {
        Action act = () => DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Guid.Empty, "One claim", originIncident: null,
            [], RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*project*");
    }

    [Fact]
    public void Superseding_records_why_and_what_replaced_it_without_deleting_anything()
    {
        DecisionAggregate decision = Recorded("The old ruling");
        Guid replacement = DomainId.New();

        DecisionSuperseded superseded = DecisionDecider.Supersede(
            decision, replacement, "  The renumberer it served is gone  ", Owner, Now.AddDays(3));
        decision.Apply(superseded);

        decision.Status.Should().Be(DecisionStatus.Superseded);
        decision.SupersededByDecisionId.Should().Be(replacement);
        decision.SupersedeReason.Should().Be("The renumberer it served is gone");
        decision.SupersededAt.Should().Be(Now.AddDays(3));
        decision.Statement.Should().Be("The old ruling", "nothing is deleted, only its standing changes");
    }

    [Fact]
    public void A_decision_can_be_overruled_with_nothing_recorded_in_its_place()
    {
        DecisionAggregate decision = Recorded("The old ruling");

        DecisionSuperseded superseded = DecisionDecider.Supersede(
            decision, supersededBy: null, "Overruled outright; nothing replaces it", Owner, Now);

        superseded.SupersededByDecisionId.Should().BeNull(
            "an honest absence beats a pointer at the nearest plausible successor");
    }

    [Fact]
    public void Superseding_needs_a_reason()
    {
        DecisionAggregate decision = Recorded("The old ruling");

        Action act = () => DecisionDecider.Supersede(decision, supersededBy: null, "  ", Owner, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*--reason*");
    }

    [Fact]
    public void Superseding_twice_refuses_and_points_at_recording_the_newer_ruling_instead()
    {
        DecisionAggregate decision = Recorded("The old ruling");
        decision.Apply(DecisionDecider.Supersede(decision, null, "First ending", Owner, Now));

        Action act = () => DecisionDecider.Supersede(decision, null, "Second ending", Owner, Now.AddDays(1));

        act.Should().Throw<DomainConflictException>().WithMessage("*h9k decide*");
    }

    [Fact]
    public void A_decision_never_supersedes_itself_on_either_path()
    {
        Guid id = DomainId.New();

        Action recordingItself = () => DecisionDecider.Record(
            id, KnowledgeScope.Project, Project, "One claim", originIncident: null, [id],
            RecordedProvenance.FromShell(Owner), Now);
        recordingItself.Should().Throw<DomainValidationException>().WithMessage("*supersede itself*");

        DecisionAggregate decision = Recorded("One claim");
        Action supersedingItself = () =>
            DecisionDecider.Supersede(decision, decision.Id, "Because", Owner, Now);
        supersedingItself.Should().Throw<DomainValidationException>().WithMessage("*supersede itself*");
    }

    [Fact]
    public void Naming_the_same_superseded_decision_twice_refuses()
    {
        Guid replaced = DomainId.New();

        Action act = () => DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "One claim", originIncident: null,
            [replaced, replaced], RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*twice*");
    }

    [Fact]
    public void What_a_decision_supersedes_is_recorded_at_birth()
    {
        Guid first = DomainId.New();
        Guid second = DomainId.New();

        DecisionRecorded recorded = DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "The ruling that holds now",
            originIncident: null, [first, second], RecordedProvenance.FromShell(Owner), Now);

        DecisionAggregate decision = new();
        decision.Apply(recorded);

        decision.Supersedes.Should().Equal(first, second);
    }

    private static DecisionAggregate Recorded(string statement)
    {
        DecisionAggregate decision = new();
        decision.Apply(DecisionDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, statement, originIncident: null,
            [], RecordedProvenance.FromShell(Owner), Now));
        return decision;
    }
}
