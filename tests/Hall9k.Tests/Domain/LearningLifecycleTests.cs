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

    private static LearningAggregate Recorded(string statement)
    {
        LearningAggregate learning = new();
        learning.Apply(LearningDecider.Record(
            DomainId.New(), KnowledgeScope.Project, Project, statement,
            RecordedProvenance.FromShell(Owner), Now));
        return learning;
    }
}
