using FluentAssertions;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A run-earned lesson as event-sourced platform data (idea d805fd8b, piece 1; backlog 55): live
/// the moment it is recorded, with no gate an agent has to pass, and retired only by an explicit
/// act carrying a reason.
/// </summary>
public sealed class LearningLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    [Fact]
    public void A_recorded_lesson_is_active_the_moment_it_lands()
    {
        Guid id = DomainId.New();
        LearningRecorded recorded = LearningDecider.Record(
            id, KnowledgeScope.Project, Project,
            "  Integration tests need Docker running before dotnet test  ",
            RecordedProvenance.FromShell(Owner), Now);

        LearningAggregate learning = new();
        learning.Apply(recorded);

        learning.Id.Should().Be(id);
        learning.Statement.Should().Be("Integration tests need Docker running before dotnet test");
        learning.Status.Should().Be(LearningStatus.Active, "there is no quarantine and no approval step");
        learning.RetiredAt.Should().BeNull();
    }

    /// <summary>
    /// The half that separates this from <c>DecisionDecider</c>: an unattended dispatched run is
    /// exactly who should be recording lessons, so nothing here refuses one.
    /// </summary>
    [Fact]
    public void An_unattended_agent_run_records_a_lesson_and_the_provenance_says_so()
    {
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();
        RecordedProvenance fromAnAgent = new(Owner, runId, taskId, HumanAttendance.Unattended);

        LearningRecorded recorded = LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "What this run learned", fromAnAgent, Now);

        recorded.Provenance.RunId.Should().Be(runId);
        recorded.Provenance.TaskId.Should().Be(taskId);
        recorded.Provenance.Attendance.Should().Be(HumanAttendance.Unattended);
    }

    [Fact]
    public void A_lesson_typed_at_a_shell_carries_explicit_nulls_for_run_and_task()
    {
        LearningRecorded recorded = LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Owner, Owner, "Prefer a fake over a real process",
            RecordedProvenance.FromShell(Owner), Now);

        recorded.Provenance.RunId.Should().BeNull();
        recorded.Provenance.TaskId.Should().BeNull();
        recorded.Provenance.Attendance.Should().Be(HumanAttendance.Unobserved);
        recorded.Scope.Should().Be(KnowledgeScope.Owner);
        recorded.ScopeId.Should().Be(Owner);
    }

    [Fact]
    public void Recording_refuses_an_empty_claim_and_says_what_a_good_one_looks_like()
    {
        Action act = () => LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "\t", RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*h9k learn*")
            .WithMessage("*One claim*");
    }

    [Fact]
    public void Recording_refuses_a_scope_with_no_id_behind_it()
    {
        Action act = () => LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Owner, Guid.Empty, "One claim",
            RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*owner*");
    }

    [Fact]
    public void Retiring_records_why_and_deletes_nothing()
    {
        LearningAggregate learning = Recorded("The old lesson");

        LearningRetired retired = LearningDecider.Retire(
            learning, "  Graduated: the gate fails the build for it now  ", Owner, Now.AddDays(9));
        learning.Apply(retired);

        learning.Status.Should().Be(LearningStatus.Retired);
        learning.RetireReason.Should().Be("Graduated: the gate fails the build for it now");
        learning.RetiredAt.Should().Be(Now.AddDays(9));
        learning.Statement.Should().Be("The old lesson", "retirement changes standing, never content");
    }

    [Fact]
    public void Retiring_needs_a_reason()
    {
        LearningAggregate learning = Recorded("The old lesson");

        Action act = () => LearningDecider.Retire(learning, "   ", Owner, Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*--reason*");
    }

    [Fact]
    public void Retiring_twice_refuses_and_says_the_record_stands()
    {
        LearningAggregate learning = Recorded("The old lesson");
        learning.Apply(LearningDecider.Retire(learning, "Wrong", Owner, Now));

        Action act = () => LearningDecider.Retire(learning, "Still wrong", Owner, Now.AddDays(1));

        act.Should().Throw<DomainConflictException>().WithMessage("*already retired*");
    }

    /// <summary>
    /// The citation guard (idea d805fd8b, piece 5): distillation is the one act here that produces
    /// a claim no single run earned, so a distilled lesson that cites nothing is indistinguishable
    /// from an agent inventing doctrine and calling it a merge.
    /// </summary>
    [Fact]
    public void A_distilled_lesson_records_the_lessons_it_was_merged_out_of()
    {
        Guid firstSource = DomainId.New();
        Guid secondSource = DomainId.New();

        LearningRecorded recorded = LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project,
            "A worktree's local base-branch ref is routinely stale; name origin/ in every range",
            [firstSource, secondSource], RecordedProvenance.FromShell(Owner), Now);

        recorded.DistilledFrom.Should().Equal(firstSource, secondSource);
    }

    [Fact]
    public void An_ordinary_lesson_cites_nothing_rather_than_an_empty_list()
    {
        LearningRecorded recorded = LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, "What this run learned",
            RecordedProvenance.FromShell(Owner), Now);

        recorded.DistilledFrom.Should().BeNull(
            "an empty list would read as a distillation that cited nothing, which the decider refuses");
    }

    [Fact]
    public void A_distilled_lesson_with_no_sources_is_refused_and_named_as_a_new_claim()
    {
        Action act = () => LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project, "A claim with no evidence behind it",
            [], RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--distilled-from*")
            .WithMessage("*merge nobody can check*");
    }

    [Fact]
    public void A_distilled_lesson_cannot_be_its_own_source()
    {
        Guid id = DomainId.New();

        Action act = () => LearningDecider.RecordDistilled(
            id, KnowledgeScope.Project, Project, "Its own evidence", [id],
            RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*cannot cite itself*");
    }

    [Fact]
    public void A_repeated_source_is_refused_and_named()
    {
        Guid source = DomainId.New();

        Action act = () => LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project, "A merge", [source, source],
            RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage($"*{DomainId.Short(source)}*")
            .WithMessage("*more than once*");
    }

    [Fact]
    public void An_empty_source_id_is_refused_rather_than_recorded_as_a_citation_of_nothing()
    {
        Action act = () => LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project, "A merge", [DomainId.New(), Guid.Empty],
            RecordedProvenance.FromShell(Owner), Now);

        act.Should().Throw<DomainValidationException>().WithMessage("*empty id*");
    }

    /// <summary>Every rule <see cref="LearningDecider.Record"/> holds still holds on the distilled path.</summary>
    [Fact]
    public void A_distilled_lesson_still_needs_a_claim_and_a_scope()
    {
        Guid source = DomainId.New();

        Action blankStatement = () => LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project, "   ", [source],
            RecordedProvenance.FromShell(Owner), Now);
        Action noScopeId = () => LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Guid.Empty, "A merge", [source],
            RecordedProvenance.FromShell(Owner), Now);

        blankStatement.Should().Throw<DomainValidationException>().WithMessage("*One claim*");
        noScopeId.Should().Throw<DomainValidationException>().WithMessage("*project*");
    }

    [Fact]
    public void The_aggregate_carries_the_citations_forward()
    {
        Guid source = DomainId.New();
        LearningAggregate learning = new();

        learning.Apply(LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project, "A merge", [source],
            RecordedProvenance.FromShell(Owner), Now));

        learning.DistilledFrom.Should().Equal(source);
    }

    /// <summary>
    /// Merging does not end what it merged: retirement stays the one terminal act and it stays
    /// explicit, so a source left live keeps riding in prompts until somebody says why it should
    /// not. That is the trade distillation shipped without a Superseded status for.
    /// </summary>
    [Fact]
    public void A_merged_source_is_still_active_until_it_is_explicitly_retired()
    {
        LearningAggregate source = Recorded("One of two overlapping claims");
        LearningDecider.RecordDistilled(
            DomainId.New(), KnowledgeScope.Project, Project, "The merged claim", [source.Id],
            RecordedProvenance.FromShell(Owner), Now);

        source.Status.Should().Be(LearningStatus.Active);

        source.Apply(LearningDecider.Retire(source, "Absorbed into the merged claim", Owner, Now.AddMinutes(1)));

        source.Status.Should().Be(LearningStatus.Retired);
    }

    private static LearningAggregate Recorded(string statement)
    {
        LearningAggregate learning = new();
        learning.Apply(LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, statement,
            RecordedProvenance.FromShell(Owner), Now));
        return learning;
    }
}
