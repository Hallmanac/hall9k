using FluentAssertions;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
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

    /// <summary>
    /// The recording node is projected off the event's own metadata, not off the event's shape
    /// (idea d805fd8b, piece 5): the prompt feed has to tell this node's own agent from another
    /// node's, and a stream replay per lesson to answer that would make composing one prompt an
    /// unbounded read.
    /// </summary>
    [Fact]
    public void A_lesson_carries_the_node_that_appended_it()
    {
        LearningDetailsProjection projection = new();
        Guid nodeId = DomainId.New();
        FakeEvent<LearningRecorded> recorded = Stamped(nodeId);

        LearningDetails view = projection.Create(recorded);

        view.RecordedOnNodeId.Should().Be(nodeId);
    }

    /// <summary>
    /// On a replicated lesson the stamping listener has already written the RECEIVING node over
    /// its own header, so the preserved replication origin is the only header that still names the
    /// node the lesson actually came from. Reading the wrong one would mark every other node's
    /// lesson as this node's and inject it.
    /// </summary>
    [Fact]
    public void A_replicated_lesson_carries_the_node_it_came_from_rather_than_the_one_that_applied_it()
    {
        LearningDetailsProjection projection = new();
        Guid originNode = DomainId.New();
        Guid receivingNode = DomainId.New();
        FakeEvent<LearningRecorded> recorded = Stamped(receivingNode);
        recorded.SetHeader(ReplicationEventHeaders.OriginNodeId, originNode.ToString());

        LearningDetails view = projection.Create(recorded);

        view.RecordedOnNodeId.Should().Be(originNode);
    }

    /// <summary>
    /// The sending node stamps its own empty id while it is still bootstrapping, and the inbox
    /// carries that forward faithfully, so a replicated lesson can arrive with a replication
    /// origin header that names nobody. Falling back to the stamped header there would answer with
    /// the RECEIVING node and present another install's lesson as this one's own, which is the
    /// worst possible wrong answer for a reader deciding whether an agent it controls wrote
    /// something.
    /// </summary>
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void A_replicated_lesson_whose_origin_is_unreadable_never_falls_back_to_the_receiving_node(
        string originHeader)
    {
        LearningDetailsProjection projection = new();
        Guid receivingNode = DomainId.New();
        FakeEvent<LearningRecorded> recorded = Stamped(receivingNode);
        recorded.SetHeader(ReplicationEventHeaders.OriginNodeId, originHeader);

        LearningDetails view = projection.Create(recorded);

        view.RecordedOnNodeId.Should().BeNull();
    }

    /// <summary>
    /// A lesson appended before the origin stamping existed, or one stamped while an install was
    /// still bootstrapping, has no readable node. That answers null rather than the asking node,
    /// which is what keeps it out of prompts instead of silently claimed as local.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void An_unreadable_or_bootstrapping_node_header_projects_as_no_node_at_all(string? header)
    {
        LearningDetailsProjection projection = new();
        FakeEvent<LearningRecorded> recorded = new(new LearningRecorded(
            DomainId.New(), KnowledgeScope.Project, Project, "One claim",
            RecordedProvenance.FromShell(Owner), Now));
        if (header is not null)
        {
            recorded.SetHeader(EventOriginStampingListener.NodeIdHeader, header);
        }

        LearningDetails view = projection.Create(recorded);

        view.RecordedOnNodeId.Should().BeNull();
    }

    [Fact]
    public void A_distilled_lessons_citations_reach_the_row_both_in_order_and_out_of_it()
    {
        LearningDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid source = DomainId.New();
        LearningRecorded distilled = new(
            id, KnowledgeScope.Project, Project, "The merged claim",
            RecordedProvenance.FromShell(Owner), Now, [source]);

        LearningDetails created = projection.Create(new FakeEvent<LearningRecorded>(distilled));
        LearningDetails applied = new();
        projection.Apply(new FakeEvent<LearningRecorded>(distilled), applied);

        created.DistilledFrom.Should().Equal(source);
        applied.DistilledFrom.Should().Equal(source);
    }

    private static FakeEvent<LearningRecorded> Stamped(Guid nodeId)
    {
        FakeEvent<LearningRecorded> recorded = new(new LearningRecorded(
            DomainId.New(), KnowledgeScope.Project, Project, "One claim",
            RecordedProvenance.FromShell(Owner), Now));
        recorded.SetHeader(EventOriginStampingListener.NodeIdHeader, nodeId.ToString());
        return recorded;
    }
}
