using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Adoption is selective, never mirroring (PLAN.md §3.1a): the platform tracks only the external
/// work someone will actually take on, and it tracks each item once. The duplicate refusal needs
/// the real projection because it is a query over what <c>TaskAdded</c> wrote, and the canonical
/// reference is the join.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskAdoptionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_second_adoption_of_the_same_issue_points_at_the_task_that_already_has_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#42");
        Guid firstTaskId = await AdoptAsync(store, issue, "Adopt existing GitHub issues", cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("github:Hallmanac/hall9k#42")
            .And.Contain(TaskListCommand.ShortId(firstTaskId))
            .And.Contain("Adopt existing GitHub issues", "the refusal names the work, not just an id");
    }

    [Fact]
    public async Task An_issue_nobody_adopted_passes_even_when_other_adoptions_exist()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        await AdoptAsync(store, new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#7"),
            "Something else entirely", cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> adoption = () => TaskAddCommand.RefuseSecondAdoptionAsync(
            session, new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#8"), cts.Token);

        await adoption.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_issue_whose_only_task_was_abandoned_can_be_adopted_again()
    {
        // The reason to refuse a second adoption is the contradiction two closeouts would make.
        // An abandoned task will never close out and will never run again, so holding the issue
        // hostage to it makes the work permanently unadoptable and buys nothing: walking away is
        // exactly how a human says they are done with it.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#99");
        Guid abandonedTaskId = await AdoptAsync(store, issue, "A first pass nobody finished", cts.Token);
        await AbandonAsync(store, abandonedTaskId, cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        await secondAdoption.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_failed_first_adoption_still_holds_the_issue()
    {
        // Failed is a waypoint, not an ending (Decisions Log #27): retry, resolve and abandon are
        // all still open on the task that has the issue, so a second task against it would be the
        // duplicate the refusal exists to prevent.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#100");
        Guid failedTaskId = await AdoptAsync(store, issue, "A pass that failed", cts.Token);
        await AppendAsync(store, failedTaskId,
            new TaskFailed(failedTaskId, DomainId.New(), "The verification never passed.", Now), cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("Failed", "the refusal says which task holds the issue and where it stands")
            .And.Contain("h9k task abandon", "and how to release it");
    }

    [Fact]
    public async Task A_done_first_adoption_is_told_to_write_a_separate_task_rather_than_to_abandon()
    {
        // A reopened GitHub issue lands here: the first adoption closed out, so it still holds the
        // reference, and TaskDecider.Abandon refuses an already-terminal task. Naming abandon would
        // send the human straight into a second refusal, which is the one error shape an agent
        // cannot self-correct from — so the route that works is the one the message offers.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference issue = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#101");
        Guid doneTaskId = await AdoptAsync(store, issue, "A pass that shipped", cts.Token);
        await AppendAsync(store, doneTaskId,
            new TaskCompleted(doneTaskId, DomainId.New(), "https://github.com/Hallmanac/hall9k/pull/9", Now),
            cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, issue, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("Done", "the refusal says where the holding task stands")
            .And.Contain("write a separate task")
            .And.NotContain("h9k task abandon", "a Done task cannot be abandoned, so offering it is a dead end");
    }

    [Fact]
    public async Task A_done_pr_review_does_not_block_a_fresh_review_of_the_same_pull_request()
    {
        // TaskDecider.Reopen refuses to reopen a Done pr-review task, and points the owner here
        // instead: "Start a fresh review instead with h9k task add --from-pr." A completed review
        // is not the same claim a Done adopted-work task makes — it says the review finished, not
        // that the pull request's own work is done — so it must not hold its reference the way an
        // ordinary Done adoption does, or that lever the decider names would refuse too and strand
        // the owner with no way to re-review the pull request.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference pullRequest = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#142");
        Guid firstReviewId = await AdoptAsync(
            store, pullRequest, "Review pull request #142", TaskType.PrReview, cts.Token);
        await AppendAsync(store, firstReviewId,
            new TaskCompleted(firstReviewId, DomainId.New(), null, Now), cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, pullRequest, cts.Token);

        await secondAdoption.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_live_pr_review_still_blocks_a_second_adoption_of_the_same_pull_request()
    {
        // The exception is for a *completed* review only. While a review is still in flight there
        // is no reason to run a second one over the same pull request concurrently.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference pullRequest = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#155");
        Guid reviewId = await AdoptAsync(
            store, pullRequest, "Review pull request #155", TaskType.PrReview, cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondAdoption = () =>
            TaskAddCommand.RefuseSecondAdoptionAsync(session, pullRequest, cts.Token);

        (await secondAdoption.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain(TaskListCommand.ShortId(reviewId));
    }

    /// <summary>
    /// The rule is about the item, not about how the task got it — so a card a task caused to
    /// exist holds that card exactly as an imported one does, and <c>h9k task link-jira</c> asks
    /// the same question <c>--from-jira</c> does before recording a key.
    /// <para>
    /// The publication prompt makes this the likely mistake rather than an exotic one: a session
    /// is told to search for an earlier attempt's card before creating a second, and a session
    /// that finds another task's card and reports its key back in good faith would otherwise put
    /// two live tasks on one card, with two sets of runs and two closeout comments on it. Origin
    /// incident (2026-08-21): the pre-PR review of this branch found link-jira checking only the
    /// task in front of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_a_task_was_linked_to_is_held_as_firmly_as_one_it_imported()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        ExternalReference card = new(WorkItemProvider.Jira, "PROJ-123");
        Guid holderId = await AdoptAsync(store, reference: null, "The task the card was made for", cts.Token);
        await AppendAsync(store, holderId,
            new WorkItemLinked(holderId, card, "Publish me", "To Do (open)", Now, Now, DomainId.New()),
            cts.Token);

        await using IQuerySession session = store.QuerySession();
        Func<Task> secondClaim = () => TaskAddCommand.RefuseSecondAdoptionAsync(session, card, cts.Token);

        (await secondClaim.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("jira:PROJ-123")
            .And.Contain(TaskListCommand.ShortId(holderId))
            .And.Contain("The task the card was made for");
    }

    private static Task AbandonAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken) =>
        AppendAsync(store, taskId, new TaskAbandoned(taskId, "Overtaken by events.", Now, DomainId.New()),
            cancellationToken);

    private static async Task AppendAsync(
        IDocumentStore store, Guid taskId, object @event, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(taskId, @event);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static Task<Guid> AdoptAsync(
        IDocumentStore store, ExternalReference? reference, string objective, CancellationToken cancellationToken) =>
        AdoptAsync(store, reference, objective, TaskType.Feature, cancellationToken);

    private static async Task<Guid> AdoptAsync(
        IDocumentStore store, ExternalReference? reference, string objective, TaskType type,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId,
            DomainId.New(),
            objective,
            ["The importer refuses a closed issue"],
            type,
            agentContext: null,
            constraints: null,
            reference,
            Now,
            DomainId.New()));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }
}
