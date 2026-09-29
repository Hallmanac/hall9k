using FluentAssertions;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The verified sender of a replicated fact reaches the read models a prompt is built from, off the
/// header this node's own inbox stamped and never off a field of the payload (security review idea
/// 6be68ee2, prompt-builders findings 1 to 6). Proved with no database, through the same
/// <see cref="FakeEvent{T}"/> stub every other projection test uses.
/// </summary>
public sealed class ReplicatedSenderProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sender = ForeignNoteFixtures.TeammateNode;

    [Fact]
    public void A_native_event_has_no_sender()
    {
        ReplicatedSender.Of(new FakeEvent<TaskRetried>(Retried("try again"))).Should().BeNull();
    }

    [Fact]
    public void A_replicated_event_answers_with_the_node_that_sent_it()
    {
        ReplicatedSender.Of(ForeignNoteFixtures.Replicated(Retried("try again"), Sender)).Should().Be(Sender);
    }

    /// <summary>
    /// A row applied before the inbox stamped the sender still carries the origin event id, and
    /// reading it as native would let a teammate's text through under the local operator's heading.
    /// The empty id belongs to no fleet, which is the fail-closed reading.
    /// </summary>
    [Fact]
    public void A_replicated_event_with_no_recorded_sender_answers_with_the_empty_node()
    {
        FakeEvent<TaskRetried> @event = new(Retried("try again"));
        @event.SetHeader(ReplicationEventHeaders.OriginEventId, Guid.NewGuid().ToString());

        ReplicatedSender.Of(@event).Should().Be(Guid.Empty);
    }

    [Fact]
    public void A_sender_header_that_will_not_parse_is_the_empty_node_and_not_native()
    {
        FakeEvent<TaskRetried> @event = new(Retried("try again"));
        @event.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, "not-a-guid");

        ReplicatedSender.Of(@event).Should().Be(Guid.Empty);
    }

    [Fact]
    public void The_claimed_origin_is_read_beside_the_sender_and_differs_when_a_record_was_forwarded()
    {
        FakeEvent<TaskRetried> forwarded = ForeignNoteFixtures.Replicated(Retried("relayed"), ForeignNoteFixtures.LocalSecondNode);
        forwarded.SetHeader(ReplicationEventHeaders.OriginNodeId, Sender.ToString());

        ReplicatedSender.Of(forwarded).Should().Be(ForeignNoteFixtures.LocalSecondNode);
        ReplicatedSender.OriginOf(forwarded).Should().Be(Sender);
        ReplicatedSender.OriginOf(new FakeEvent<TaskRetried>(Retried("mine"))).Should().BeNull();
        FakeEvent<TaskRetried> unreadable = ForeignNoteFixtures.Replicated(Retried("odd"), Sender);
        unreadable.SetHeader(ReplicationEventHeaders.OriginNodeId, "nonsense");
        ReplicatedSender.OriginOf(unreadable).Should().Be(Guid.Empty);
    }

    [Fact]
    public void A_forwarded_retry_projects_both_the_sender_and_where_it_began()
    {
        TaskDetailsProjection projection = new();
        TaskDetails view = NewTask(projection);
        FakeEvent<TaskRetried> forwarded = ForeignNoteFixtures.Replicated(Retried("relayed"), ForeignNoteFixtures.LocalSecondNode);
        forwarded.SetHeader(ReplicationEventHeaders.OriginNodeId, Sender.ToString());

        projection.Apply(forwarded, view);

        view.RetryReceivedFromNodeId.Should().Be(ForeignNoteFixtures.LocalSecondNode);
        view.RetryOriginNodeId.Should().Be(Sender);
    }

    [Fact]
    public void A_retry_carries_its_own_sender_and_the_payload_author_is_not_consulted()
    {
        TaskDetailsProjection projection = new();
        TaskDetails view = NewTask(projection);
        Guid claimedAuthor = ForeignNoteFixtures.LocalRootNode;

        projection.Apply(
            ForeignNoteFixtures.Replicated(Retried("prioritize the migration", claimedAuthor), Sender), view);

        view.RetryReceivedFromNodeId.Should().Be(
            Sender, "the payload's RetriedByOwnerId is whatever the sender wrote, the header is what this node verified");
    }

    [Fact]
    public void A_native_retry_after_a_replicated_one_clears_the_sender()
    {
        TaskDetailsProjection projection = new();
        TaskDetails view = NewTask(projection);
        projection.Apply(ForeignNoteFixtures.Replicated(Retried("theirs"), Sender), view);

        projection.Apply(new FakeEvent<TaskRetried>(Retried("mine")), view);

        view.RetryReason.Should().Be("mine");
        view.RetryReceivedFromNodeId.Should().BeNull("the standing reason is the local operator's own again");
    }

    [Fact]
    public void A_handback_carries_its_own_sender()
    {
        TaskDetailsProjection projection = new();
        TaskDetails view = NewTask(projection);

        projection.Apply(
            ForeignNoteFixtures.Replicated(
                new TaskHandedBack(view.Id, DomainId.New(), "task/abc-do", "out of time", Now, DomainId.New()), Sender),
            view);

        view.RetryReceivedFromNodeId.Should().Be(Sender);
        view.RetryReasonIsHandback.Should().BeTrue();
    }

    [Fact]
    public void A_handoff_note_carries_its_own_sender_apart_from_the_retry_reason()
    {
        TaskDetailsProjection projection = new();
        TaskDetails view = NewTask(projection);

        projection.Apply(
            ForeignNoteFixtures.Replicated(
                new TaskHandoffNoted(view.Id, "the migration is half done", Sender, ForeignNoteFixtures.TeammateRoot, Now),
                Sender),
            view);

        view.HandoffNoteReceivedFromNodeId.Should().Be(Sender);
        view.RetryReceivedFromNodeId.Should().BeNull("the two fields are judged independently");
    }

    [Fact]
    public void A_lesson_carries_its_own_sender()
    {
        LearningDetailsProjection projection = new();

        LearningDetails replicated = projection.Create(ForeignNoteFixtures.Replicated(Lesson(), Sender));
        LearningDetails native = projection.Create(new FakeEvent<LearningRecorded>(Lesson()));

        replicated.ReceivedFromNodeId.Should().Be(Sender);
        native.ReceivedFromNodeId.Should().BeNull();
    }

    private static TaskDetails NewTask(TaskDetailsProjection projection) =>
        projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            DomainId.New(), DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

    private static TaskRetried Retried(string reason, Guid? by = null) =>
        new(DomainId.New(), null, "task/abc-do", reason, Now, by ?? DomainId.New());

    private static LearningRecorded Lesson() => new(
        DomainId.New(), KnowledgeScope.Project, DomainId.New(), "a claim", RecordedProvenance.FromShell(DomainId.New()), Now);
}
