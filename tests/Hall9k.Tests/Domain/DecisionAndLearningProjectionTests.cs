using FluentAssertions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The two read models both surfaces use, built without a database. What each row has to carry
/// beyond its statement is what the list filters on (scope, scope id, status, recorded-at) and
/// what show renders (provenance, and for a decision both directions of supersession).
/// </summary>
public sealed class DecisionAndLearningProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly Guid Project = DomainId.New();

    [Fact]
    public void A_recorded_decision_projects_every_field_the_list_filters_on()
    {
        DecisionDetailsProjection projection = new();
        Guid id = DomainId.New();

        DecisionDetails view = projection.Create(new FakeEvent<DecisionRecorded>(new DecisionRecorded(
            id, KnowledgeScope.Project, Project, "One claim", "An incident", [],
            RecordedProvenance.FromShell(Owner), Now)));

        view.Id.Should().Be(id);
        view.Scope.Should().Be(KnowledgeScope.Project);
        view.ScopeId.Should().Be(Project);
        view.Status.Should().Be(DecisionStatus.Recorded);
        view.RecordedAt.Should().Be(Now);
        view.OriginIncident.Should().Be("An incident");
        view.Provenance!.RunId.Should().BeNull();
    }

    [Fact]
    public void Superseding_moves_the_row_and_records_both_the_reason_and_the_successor()
    {
        DecisionDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid replacement = DomainId.New();

        DecisionDetails view = projection.Create(new FakeEvent<DecisionRecorded>(new DecisionRecorded(
            id, KnowledgeScope.Project, Project, "The old ruling", null, [],
            RecordedProvenance.FromShell(Owner), Now)));
        projection.Apply(
            new FakeEvent<DecisionSuperseded>(new DecisionSuperseded(
                id, replacement, "Replaced", Owner, Now.AddDays(2))),
            view);

        view.Status.Should().Be(DecisionStatus.Superseded);
        view.SupersededByDecisionId.Should().Be(replacement);
        view.SupersedeReason.Should().Be("Replaced");
        view.SupersededAt.Should().Be(Now.AddDays(2));
        view.Statement.Should().Be("The old ruling");
    }

    /// <summary>
    /// The out-of-order shape a replicated stream can produce: the supersession arrives and
    /// materializes a document before the recording that started the stream does. The recording
    /// must fill in everything it is authoritative for without undoing the ending, which is the
    /// later fact whichever order the two landed in.
    /// </summary>
    [Fact]
    public void A_recording_processed_after_its_own_supersession_never_undoes_the_ending()
    {
        DecisionDetailsProjection projection = new();
        Guid id = DomainId.New();
        DecisionDetails view = new();

        projection.Apply(
            new FakeEvent<DecisionSuperseded>(new DecisionSuperseded(
                id, null, "Overruled", Owner, Now.AddDays(2))),
            view);
        projection.Apply(
            new FakeEvent<DecisionRecorded>(new DecisionRecorded(
                id, KnowledgeScope.Project, Project, "The old ruling", null, [],
                RecordedProvenance.FromShell(Owner), Now)),
            view);

        view.Statement.Should().Be("The old ruling", "the recording is authoritative for the claim");
        view.ScopeId.Should().Be(Project);
        view.Status.Should().Be(DecisionStatus.Superseded, "the ending is the later fact either way");
    }

    [Fact]
    public void A_recorded_lesson_projects_every_field_the_list_filters_on()
    {
        LearningDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();

        LearningDetails view = projection.Create(new FakeEvent<LearningRecorded>(new LearningRecorded(
            id, KnowledgeScope.Owner, Owner, "One claim",
            new RecordedProvenance(Owner, runId, taskId, HumanAttendance.Unattended), Now)));

        view.Id.Should().Be(id);
        view.Scope.Should().Be(KnowledgeScope.Owner);
        view.ScopeId.Should().Be(Owner);
        view.Status.Should().Be(LearningStatus.Active);
        view.RecordedAt.Should().Be(Now);
        view.Provenance!.RunId.Should().Be(runId);
        view.Provenance.TaskId.Should().Be(taskId);
        view.Provenance.Attendance.Should().Be(HumanAttendance.Unattended);
    }

    [Fact]
    public void Retiring_moves_the_row_and_keeps_the_statement()
    {
        LearningDetailsProjection projection = new();
        Guid id = DomainId.New();

        LearningDetails view = projection.Create(new FakeEvent<LearningRecorded>(new LearningRecorded(
            id, KnowledgeScope.Project, Project, "The old lesson",
            RecordedProvenance.FromShell(Owner), Now)));
        projection.Apply(
            new FakeEvent<LearningRetired>(new LearningRetired(id, "Wrong", Owner, Now.AddDays(4))),
            view);

        view.Status.Should().Be(LearningStatus.Retired);
        view.RetireReason.Should().Be("Wrong");
        view.RetiredAt.Should().Be(Now.AddDays(4));
        view.Statement.Should().Be("The old lesson");
    }

    [Fact]
    public void A_lesson_recording_processed_after_its_own_retirement_never_undoes_the_ending()
    {
        LearningDetailsProjection projection = new();
        Guid id = DomainId.New();
        LearningDetails view = new();

        projection.Apply(
            new FakeEvent<LearningRetired>(new LearningRetired(id, "Wrong", Owner, Now.AddDays(4))),
            view);
        projection.Apply(
            new FakeEvent<LearningRecorded>(new LearningRecorded(
                id, KnowledgeScope.Project, Project, "The old lesson",
                RecordedProvenance.FromShell(Owner), Now)),
            view);

        view.Statement.Should().Be("The old lesson");
        view.Status.Should().Be(LearningStatus.Retired);
    }
}
