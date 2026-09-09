using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What a second <c>h9k task add --from-pr</c> on the same pull request answers with (task: a
/// pr-review task stays open while the pull request's review threads are unresolved): the task
/// that already holds it, rather than a second one that knows nothing about the review it is
/// repeating.
/// <para>
/// Origin gap (2026-09-08, arx-platform #2023): the only lever after a review closed out was a
/// fresh adoption, which quietly abandoned the first task's findings, its verdict, and its whole
/// record on the same pull request — and, for a review still waiting on its author, would have
/// put two records of one conversation on the board.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class RepeatPullRequestAdoptionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A pull request of this test's own. The one-live-task-per-item rule is keyed on the
    /// canonical reference and is deliberately project-blind, so two tests naming the same
    /// <c>owner/repo#42</c> against this class's shared fixture would read each other's tasks.
    /// </summary>
    private static ExternalReference NewReference() =>
        new(WorkItemProvider.GitHubPullRequest, $"acme/web-{Guid.NewGuid():N}#42");

    /// <summary>
    /// A pr-review task on <paramref name="reference"/>, taken as far as
    /// <paramref name="through"/> — which is the whole variable this class is about.
    /// </summary>
    private async Task<Guid> SeedPrReviewTaskAsync(
        ExternalReference reference, string through, CancellationToken cancellationToken)
    {
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"repeat-adoption-{taskId:N}", "/tmp/repeat-adoption-repo",
            new Uri("https://github.com/acme/web"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {reference.Reference}", ["every finding is directed"],
            TaskType.PrReview, null, null, reference, Now.AddDays(-1), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        List<object> events = [added];

        if (through != "Draft")
        {
            TaskPublished published = TaskDecider.Publish(
                task, TaskDependencyGraph.Empty, Now.AddDays(-1), node.OwnerId, BacklogPolicy.None);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddDays(-1), node.OwnerId);
            task.Apply(assigned);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddDays(-1));
            task.Apply(claimed);
            events.AddRange([published, assigned, claimed]);
        }

        if (through is "AwaitingAuthor" or "NeedsHuman" or "Done")
        {
            PullRequestReviewFollowThroughOpened opened = TaskDecider.OpenPrReviewFollowThrough(
                task, runId, $"https://github.com/{reference.Reference.Replace("#", "/pull/")}", "aaa", Now.AddHours(-2));
            task.Apply(opened);
            events.Add(opened);
        }

        if (through == "NeedsHuman")
        {
            PullRequestReviewFollowThroughObserved observed = TaskDecider.ObservePrReviewFollowThrough(
                task, "brian", [new PrReviewThreadWatermark("T1", 1, IsResolved: false)],
                reReviewRequested: false, headSha: "aaa", commitCount: 1, Now.AddHours(-1));
            task.Apply(observed);
            PullRequestReviewAuthorResponded responded = TaskDecider.RecordPrReviewAuthorResponse(
                task, "The author answered your review.", replyCount: 1, threadsWithReplies: 1,
                newCommitCount: null, headMoved: false, reReviewNewlyRequested: false,
                interactiveSessionAddress: null, Now);
            task.Apply(responded);
            events.AddRange([observed, responded]);
        }

        // An ordinary findings park: NeedsHuman with no follow-through open, which is the holder
        // the guard's exemption must NOT let past.
        if (through == "NeedsHumanQuestion")
        {
            QuestionAsked asked = TaskDecider.Ask(
                task, DomainId.New(), runId, "which of the two readings did you mean?", Now.AddHours(-1));
            task.Apply(asked);
            events.Add(asked);
        }

        if (through == "Done")
        {
            TaskCompleted completed = TaskDecider.Complete(task, runId, "https://github.com/acme/web/pull/42", Now);
            task.Apply(completed);
            events.Add(completed);
        }

        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    [Theory]
    [InlineData("AwaitingAuthor")]
    [InlineData("NeedsHuman")]
    [InlineData("Done")]
    public async Task A_repeat_adoption_names_the_task_that_already_holds_the_pull_request(string state)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        Guid taskId = await SeedPrReviewTaskAsync(reference, state, cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        TaskListItem? holder = await TaskAddCommand.FindPrReviewHolderAsync(session, reference, cts.Token);

        holder.Should().NotBeNull();
        holder!.Id.Should().Be(taskId);
        TaskAddCommand.NextStepFor(holder, reference, "abcd1234").Should().NotBeEmpty(
            "each state's own next step is what the output says instead of minting a task");
    }

    [Fact]
    public async Task The_named_task_carries_the_step_that_reads_only_what_changed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        await SeedPrReviewTaskAsync(reference, "NeedsHuman", cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        TaskListItem holder = (await TaskAddCommand.FindPrReviewHolderAsync(session, reference, cts.Token))!;

        TaskAddCommand.NextStepFor(holder, reference, "abcd1234").Should().Contain("--since-my-review");
    }

    [Fact]
    public async Task A_waiting_reviews_next_step_is_the_watch_rather_than_a_command_to_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        await SeedPrReviewTaskAsync(reference, "AwaitingAuthor", cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        TaskListItem holder = (await TaskAddCommand.FindPrReviewHolderAsync(session, reference, cts.Token))!;

        TaskAddCommand.NextStepFor(holder, reference, "abcd1234").Should()
            .Contain("closeout watcher is polling")
            .And.NotContain("--since-my-review", "nothing has changed for a scoped lap to read yet");
    }

    [Fact]
    public async Task A_completed_reviews_next_step_names_the_deliberate_second_adoption()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        await SeedPrReviewTaskAsync(reference, "Done", cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        TaskListItem holder = (await TaskAddCommand.FindPrReviewHolderAsync(session, reference, cts.Token))!;

        TaskAddCommand.NextStepFor(holder, reference, "abcd1234").Should().Contain("--again");
    }

    [Fact]
    public async Task An_in_flight_review_is_still_refused_rather_than_named()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        await SeedPrReviewTaskAsync(reference, "Claimed", cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        (await TaskAddCommand.FindPrReviewHolderAsync(session, reference, cts.Token)).Should().BeNull(
            "a review that is still running is in-flight work, and duplicating it is the contradiction the "
            + "second-adoption guard exists for");
        Func<Task> act = () => TaskAddCommand.RefuseSecondAdoptionAsync(session, reference, cts.Token);
        await act.Should().ThrowAsync<DomainConflictException>();
    }

    /// <summary>
    /// <c>--again</c>'s own case (independent pre-PR review, cycle 1, adversarial lens). The flag
    /// skips the naming and goes straight to the second-adoption guard, so that guard has to let
    /// exactly the three named-not-duplicated holders past — without it the flag refused two of
    /// the three cases its own help names, with a conflict that never mentioned it.
    /// </summary>
    [Theory]
    [InlineData("AwaitingAuthor")]
    [InlineData("NeedsHuman")]
    [InlineData("Done")]
    public async Task A_deliberate_second_adoption_is_not_refused_by_the_guard(string state)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        await SeedPrReviewTaskAsync(reference, state, cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        Func<Task> act = () => TaskAddCommand.RefuseSecondAdoptionAsync(session, reference, cts.Token);

        await act.Should().NotThrowAsync(
            "h9k task add --from-pr --again is the deliberate second review of a pull request one of "
            + "these three holders already carries, and it is the only route that reaches this guard "
            + "with one of them on record");
    }

    /// <summary>
    /// The other side of that exemption: NeedsHuman is only exempt with the follow-through
    /// actually open. An ordinary findings park — or any other NeedsHuman task — keeps the refusal
    /// it has always had, including on a document written before the flag existed, which is why
    /// the guard's own NULL handling is explicit.
    /// </summary>
    [Fact]
    public async Task A_needs_human_task_with_no_follow_through_is_still_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference reference = NewReference();
        await SeedPrReviewTaskAsync(reference, "NeedsHumanQuestion", cts.Token);

        await using IQuerySession session = postgres.Store.QuerySession();
        (await TaskAddCommand.FindPrReviewHolderAsync(session, reference, cts.Token)).Should().BeNull(
            "a park nobody has walked is not a review being followed through");
        Func<Task> act = () => TaskAddCommand.RefuseSecondAdoptionAsync(session, reference, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>();
    }

    [Fact]
    public async Task An_issue_is_never_answered_by_this_rule()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        ExternalReference issue = new(WorkItemProvider.GitHub, $"acme/web-{Guid.NewGuid():N}#42");

        await using IQuerySession session = postgres.Store.QuerySession();
        (await TaskAddCommand.FindPrReviewHolderAsync(session, issue, cts.Token)).Should().BeNull(
            "only a pull request has a review to be following through on");
    }
}
