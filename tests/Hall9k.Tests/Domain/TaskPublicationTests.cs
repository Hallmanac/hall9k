using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Publishing a task as a card, and the gate that decides what the platform is willing to
/// believe about the result (backlog 18).
/// <para>
/// The rule underneath every test here is one sentence: an agent's claim is an argument to a
/// command, never the recorded fact. So the decider's job is not to judge a key — it never sees
/// Jira — but to decide whether the task is in a position to accept one, and to make two cards
/// for one task impossible to reach by accident.
/// </para>
/// </summary>
public sealed class TaskPublicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();
    private static readonly ExternalReference Card = new(WorkItemProvider.Jira, "PROJ-123");

    private static TaskAggregate Draft(ExternalReference? adopted = null)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Connect Jira as a work-item source", ["It imports a card"],
            TaskType.Feature, agentContext: null, constraints: null, adopted, Now, Owner));
        return task;
    }

    private static WorkItemPublicationRequested Request(TaskAggregate task, string board = "PROJ") =>
        TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse(board), Now, Owner);

    [Fact]
    public void A_draft_can_be_published_because_a_board_is_where_work_becomes_visible()
    {
        // Deliberately not gated on Published. A card is how a team sees that work exists, and a
        // draft is exactly the stage where somebody wants that visible; tying it to the readiness
        // gate would tie a Jira board to a contract that has nothing to do with it.
        TaskAggregate task = Draft();

        WorkItemPublicationRequested requested = Request(task);

        requested.Provider.Should().Be(WorkItemProvider.Jira);
        requested.ProjectKey.Value.Should().Be("PROJ");
    }

    [Fact]
    public void A_second_request_is_refused_while_one_is_outstanding()
    {
        // Two sessions would create two cards, and a duplicate card is a human's cleanup rather
        // than a retry.
        TaskAggregate task = Draft();
        task.Apply(Request(task));

        Action again = () => Request(task);

        again.Should().Throw<DomainConflictException>().WithMessage("*already has a jira publication outstanding*");
    }

    [Fact]
    public void A_task_that_already_carries_an_item_is_not_published_again()
    {
        TaskAggregate adopted = Draft(new ExternalReference(WorkItemProvider.GitHub, "o/r#42"));

        Action publish = () => Request(adopted);

        publish.Should().Throw<DomainConflictException>().WithMessage("*already linked to github:o/r#42*");
    }

    [Fact]
    public void An_abandoned_task_is_not_put_on_somebody_else_s_board()
    {
        TaskAggregate task = Draft();
        task.Apply(TaskDecider.Abandon(task, "Superseded", Now, Owner));

        Action publish = () => Request(task);

        publish.Should().Throw<DomainConflictException>().WithMessage("*no work to put on a board*");
    }

    [Fact]
    public void Dispatching_the_session_is_what_stops_the_next_sweep_dispatching_another()
    {
        TaskAggregate task = Draft();
        task.Apply(Request(task));
        task.PublicationSessionDispatched.Should().BeFalse("nothing has been spawned yet");

        task.Apply(new WorkItemPublicationDispatched(task.Id, DomainId.New(), DomainId.New(), Now));

        task.PublicationSessionDispatched.Should().BeTrue();
    }

    [Fact]
    public void The_link_records_what_was_observed_rather_than_what_was_claimed()
    {
        TaskAggregate task = Draft();

        WorkItemLinked linked = TaskDecider.LinkWorkItem(
            task, Card, "Cards should carry their own summary", "To Do (open)", Now, Now, Owner);

        linked.Reference.Should().Be(Card);
        linked.ObservedTitle.Should().Be("Cards should carry their own summary");
        linked.ObservedStatus.Should().Be("To Do (open)");
        linked.ObservedAt.Should().Be(Now);
    }

    [Fact]
    public void Linking_ends_the_outstanding_publication_even_before_the_session_exits()
    {
        // The link is the errand's real ending. A pending marker left standing would let the next
        // sweep dispatch a second card for a task that already has one.
        TaskAggregate task = Draft();
        task.Apply(Request(task));
        task.Apply(new WorkItemPublicationDispatched(task.Id, DomainId.New(), DomainId.New(), Now));

        task.Apply(TaskDecider.LinkWorkItem(task, Card, "s", "To Do (open)", Now, Now, Owner));

        task.ExternalReference.Should().Be(Card);
        task.PendingPublicationProvider.Should().BeNull();
        task.PublicationSessionDispatched.Should().BeFalse();
    }

    [Fact]
    public void Repeating_the_same_link_is_answered_rather_than_refused()
    {
        // The caller most likely to repeat it is an agent that could not tell whether its first
        // attempt landed. "Yes, that is what I have" is the answer that lets it stop.
        TaskAggregate task = Draft();
        task.Apply(TaskDecider.LinkWorkItem(task, Card, "s", "To Do (open)", Now, Now, Owner));

        TaskDecider.AlreadyLinkedTo(task, Card).Should().BeTrue();
        TaskDecider.AlreadyLinkedTo(task, new ExternalReference(WorkItemProvider.Jira, "PROJ-999")).Should().BeFalse();
    }

    [Fact]
    public void A_different_card_on_an_already_linked_task_is_a_conflict_a_human_should_see()
    {
        TaskAggregate task = Draft();
        task.Apply(TaskDecider.LinkWorkItem(task, Card, "s", "To Do (open)", Now, Now, Owner));

        Action relink = () => TaskDecider.LinkWorkItem(
            task, new ExternalReference(WorkItemProvider.Jira, "PROJ-999"), "s", "To Do (open)", Now, Now, Owner);

        relink.Should().Throw<DomainConflictException>()
            .WithMessage("*already linked to jira:PROJ-123*")
            .WithMessage("*duplicate somebody has to close*");
    }

    [Fact]
    public void A_completed_publication_clears_the_marker_and_keeps_why()
    {
        TaskAggregate task = Draft();
        task.Apply(Request(task));

        task.Apply(new WorkItemPublicationCompleted(task.Id, false, "The session found no rule for the board.", Now));

        task.PendingPublicationProvider.Should().BeNull("the errand is over either way");
    }

    /// <summary>
    /// The projection carries the same story to the surfaces a human reads, and the row a second
    /// import is checked against. A card a task caused to exist has to block a later --from-jira
    /// of that same card exactly as an adopted one does: the two funnel exits meet on this field.
    /// </summary>
    [Fact]
    public void The_projections_carry_the_link_to_both_the_detail_pane_and_the_uniqueness_check()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, Now, Owner);
        WorkItemLinked linked = new(added.Id, Card, "Card summary", "To Do (open)", Now, Now, Owner);

        TaskDetailsProjection details = new();
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(added));
        details.Apply(new FakeEvent<WorkItemLinked>(linked), detail);

        TaskListItemProjection list = new();
        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(added));
        list.Apply(new FakeEvent<WorkItemLinked>(linked), row);

        detail.ExternalReference.Should().Be("jira:PROJ-123");
        detail.ExternalStatusObserved.Should().Be("To Do (open)");
        detail.ExternalObservedAt.Should().Be(Now);
        row.ExternalReference.Should().Be("jira:PROJ-123");
    }

    [Fact]
    public void The_detail_pane_knows_whether_a_publication_is_waiting_or_running()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, Now, Owner);

        TaskDetailsProjection projection = new();
        TaskDetails detail = projection.Create(new FakeEvent<TaskAdded>(added));
        projection.Apply(
            new FakeEvent<WorkItemPublicationRequested>(new WorkItemPublicationRequested(
                added.Id, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, Owner)),
            detail);

        detail.PendingPublicationProvider.Should().Be("jira");
        detail.PublicationSessionDispatched.Should().BeFalse();
        detail.PublicationRequestedByOwnerId.Should().Be(Owner, "a node acts on its own owner's requests");

        projection.Apply(
            new FakeEvent<WorkItemPublicationDispatched>(
                new WorkItemPublicationDispatched(added.Id, DomainId.New(), DomainId.New(), Now)),
            detail);

        detail.PublicationSessionDispatched.Should().BeTrue();
        detail.PublicationSessionProcessId.Should().BeNull(
            "the dispatch is committed before anything is spawned, so there is no process yet");

        projection.Apply(
            new FakeEvent<WorkItemPublicationSessionStarted>(
                new WorkItemPublicationSessionStarted(added.Id, DomainId.New(), 4242, Now)),
            detail);

        detail.PublicationSessionProcessId.Should().Be(4242);
        detail.PublicationSessionStartedAt.Should().Be(Now);
    }

    /// <summary>
    /// The window the two-event split exists to close. A session spawned before its dispatch was
    /// recorded is a live card-writer the stream has never heard of, so a crash in that window
    /// leaves the next sweep free to start a second one — and two sessions mean two cards. The
    /// marker that refuses the second dispatch is therefore set by the first event, which is
    /// committed before anything can create anything.
    /// </summary>
    [Fact]
    public void The_dispatch_marker_is_set_before_a_process_exists_to_record()
    {
        TaskAggregate task = Draft();
        task.Apply(Request(task));

        task.Apply(new WorkItemPublicationDispatched(task.Id, DomainId.New(), DomainId.New(), Now));

        task.PublicationSessionDispatched.Should().BeTrue(
            "nothing has been spawned yet, and that is exactly when the guard has to be up");
        Action second = () => TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, Owner);
        second.Should().Throw<DomainConflictException>();
    }

    /// <summary>
    /// The other order of the same two commands. Requesting a card for an abandoned task is
    /// refused by name; abandoning a task with a request outstanding used to leave the marker
    /// standing, and the marker is the only thing the daemon's sweep reads — so the refusal held
    /// for a second and the card got filed anyway, on work nobody intends to do and on a task
    /// that could not then record it (linking an abandoned task is refused too). Origin incident
    /// (2026-08-22): the pre-PR review of this branch traced it from push-to-jira with the daemon
    /// stopped, then abandon, then the daemon starting.
    /// </summary>
    [Fact]
    public void Abandoning_takes_the_publication_nobody_has_started_with_it()
    {
        TaskAggregate task = Draft();
        task.Apply(Request(task));

        task.Apply(TaskDecider.Abandon(task, "Superseded", Now, Owner));

        task.PendingPublicationProvider.Should().BeNull("the request outlived the intent behind it");
        task.PendingPublicationProjectKey.Should().Be(JiraProjectKey.None);
    }

    /// <summary>
    /// And the case where it must not: a session is already out there writing a card, and the
    /// markers are how the daemon finds it, waits for it and ends it honestly. Cleared here, it
    /// would be detached with nothing watching, which is exactly how a surprise card arrives on
    /// a board with no record of where it came from.
    /// </summary>
    [Fact]
    public void Abandoning_leaves_a_dispatched_publication_for_adoption_to_finish()
    {
        TaskAggregate task = Draft();
        task.Apply(Request(task));
        task.Apply(new WorkItemPublicationDispatched(task.Id, DomainId.New(), DomainId.New(), Now));

        task.Apply(TaskDecider.Abandon(task, "Superseded", Now, Owner));

        task.PendingPublicationProvider.Should().Be(WorkItemProvider.Jira);
        task.PublicationSessionDispatched.Should().BeTrue("adoption reads both of these to find the session");
    }

    /// <summary>
    /// The projection tells the same story, and here it matters most: this view is what the
    /// daemon's sweep queries and what h9k task show reads, so an abandoned task must stop
    /// saying a publication is waiting for the daemon.
    /// </summary>
    [Fact]
    public void The_detail_pane_drops_an_unstarted_publication_when_the_task_is_abandoned()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, Now, Owner);
        WorkItemPublicationRequested requested = new(
            added.Id, WorkItemProvider.Jira, JiraProjectKey.Parse("PROJ"), Now, Owner);

        TaskDetailsProjection projection = new();
        TaskDetails waiting = projection.Create(new FakeEvent<TaskAdded>(added));
        projection.Apply(new FakeEvent<WorkItemPublicationRequested>(requested), waiting);
        projection.Apply(
            new FakeEvent<TaskAbandoned>(new TaskAbandoned(added.Id, "Superseded", Now, Owner)), waiting);

        waiting.PendingPublicationProvider.Should().BeNull();
        waiting.PendingPublicationProjectKey.Should().Be(JiraProjectKey.None);

        TaskDetails running = projection.Create(new FakeEvent<TaskAdded>(added));
        projection.Apply(new FakeEvent<WorkItemPublicationRequested>(requested), running);
        projection.Apply(
            new FakeEvent<WorkItemPublicationDispatched>(
                new WorkItemPublicationDispatched(added.Id, DomainId.New(), DomainId.New(), Now)),
            running);
        projection.Apply(
            new FakeEvent<TaskAbandoned>(new TaskAbandoned(added.Id, "Superseded", Now, Owner)), running);

        running.PendingPublicationProvider.Should().Be("jira", "adoption queries this view for the session");
        running.PublicationSessionDispatched.Should().BeTrue();
    }
}
