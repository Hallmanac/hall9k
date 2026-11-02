using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Feedback;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Spectre.Console.Cli;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The feedback step's commands run through the real command tree against a real store: <c>h9k project set --feedback-step</c>,
/// <c>h9k task revise --feedback-step</c> with the narrower rule it carries, <c>h9k task queue</c> holding a task whose blockers
/// have all finished, the claims a held task refuses, and the surfaces that say it is held and how a step ended.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class FeedbackStepCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();
    private BootstrapContext _me = null!;
    private Guid _projectId;
    private string _projectName = string.Empty;

    public FeedbackStepCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cts.Token);
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        _me = await NodeBootstrap.EnsureAsync(session, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        _projectId = DomainId.New();
        _projectName = $"feedback-{_projectId:N}"[..20];
        await using IDocumentSession register = _postgres.Store.LightweightSession();
        register.Events.StartStream<ProjectAggregate>(_projectId, ProjectDecider.Register(
            _projectId, _me.OwnerId, DomainId.New(), _projectName, Directory.CreateTempSubdirectory("h9k-feedback-cmd-").FullName, null, "main", Now));
        await register.SaveChangesAsync(cts.Token);
    }

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Exception?> RunCliAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using ScopedConnectionString connection = new(_postgres.ConnectionString);
        CommandApp app = new();
        app.Configure(config => CliCommandTree.Configure(config, _ => null));
        try
        {
            await app.RunAsync(arguments, cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception is CommandRuntimeException { InnerException: { } inner } ? inner : exception;
        }
    }

    private async Task<ProjectDetails> ProjectAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.LoadAsync<ProjectDetails>(_projectId, cancellationToken))!;
    }

    private async Task<TaskDetails> TaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!;
    }

    /// <summary>A blocker at true closeout, and a Published task blocked by it with the project's setting as given.</summary>
    private async Task<(Guid TaskId, Guid BlockerId)> SeedPublishedAsync(CancellationToken cancellationToken)
    {
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();
        Guid taskId = DomainId.New();
        TaskAdded blockerAdded = TaskDecider.Add(blockerId, _projectId, "The blocker", ["it is done"], TaskType.Feature, "ctx", null, null, Now, _me.OwnerId);
        TaskAdded added = TaskDecider.Add(
            taskId, _projectId, "Page through the results", ["It pages"], TaskType.Feature, "ctx", null, null, Now, _me.OwnerId, blockedBy: [blockerId]);
        TaskAggregate task = new();
        task.Apply(added);
        TaskDependency dependency = new(blockerId, "The blocker", TaskState.Done, true, RunState.Completed, null, TaskType.Feature, []);
        TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph([dependency]), Now, _me.OwnerId);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(blockerId, blockerAdded);
        session.Events.StartStream<TaskAggregate>(taskId, added, published);
        await session.SaveChangesAsync(cancellationToken);
        await using IDocumentSession close = _postgres.Store.LightweightSession();
        TaskListItem row = (await close.LoadAsync<TaskListItem>(blockerId, cancellationToken))!;
        row.State = TaskState.Done;
        row.CurrentRunId = runId;
        close.Store(row);
        close.Store(new RunDetails { Id = runId, TaskId = blockerId, State = RunState.Completed });
        await close.SaveChangesAsync(cancellationToken);
        return (taskId, blockerId);
    }

    [Fact]
    public async Task The_project_setting_is_off_by_default_turns_on_and_off_and_refuses_a_word_it_does_not_know()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (await ProjectAsync(cts.Token)).TakesFeedbackStep.Should().BeFalse();

        (await RunCliAsync(cts.Token, "project", "set", _projectName, "--feedback-step", "on")).Should().BeNull();
        (await ProjectAsync(cts.Token)).TakesFeedbackStep.Should().BeTrue();
        (await RunCliAsync(cts.Token, "project", "show", _projectName)).Should().BeNull();

        (await RunCliAsync(cts.Token, "project", "set", _projectName, "--feedback-step", "off")).Should().BeNull();
        (await ProjectAsync(cts.Token)).TakesFeedbackStep.Should().BeFalse();

        Exception? refused = await RunCliAsync(cts.Token, "project", "set", _projectName, "--feedback-step", "sometimes");
        refused.Should().BeOfType<DomainValidationException>().Which.Message.Should().Contain("--feedback-step");
    }

    [Fact]
    public async Task A_tasks_override_is_set_and_cleared_while_it_is_published_and_refused_with_the_reason_once_it_is_queued()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, _) = await SeedPublishedAsync(cts.Token);
        string id = DomainId.Short(taskId);

        (await RunCliAsync(cts.Token, "task", "revise", id, "--feedback-step", "on")).Should().BeNull();
        (await TaskAsync(taskId, cts.Token)).FeedbackStepOverride.Should().BeTrue();
        (await RunCliAsync(cts.Token, "task", "revise", id, "--feedback-step", "off")).Should().BeNull();
        (await TaskAsync(taskId, cts.Token)).FeedbackStepOverride.Should().BeFalse();
        (await RunCliAsync(cts.Token, "task", "revise", id, "--feedback-step", "default")).Should().BeNull();
        (await TaskAsync(taskId, cts.Token)).FeedbackStepOverride.Should().BeNull();
        (await RunCliAsync(cts.Token, "task", "revise", id, "--feedback-step", "maybe")).Should().BeOfType<DomainValidationException>();

        (await RunCliAsync(cts.Token, "task", "queue", id)).Should().BeNull();
        (await TaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Queued, "the project does not take the step");
        Exception? refused = await RunCliAsync(cts.Token, "task", "revise", id, "--feedback-step", "on");
        refused.Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("released to Queued");
    }

    [Fact]
    public async Task Queueing_a_task_that_takes_the_step_with_every_blocker_finished_holds_it_and_nothing_can_claim_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (await RunCliAsync(cts.Token, "project", "set", _projectName, "--feedback-step", "on")).Should().BeNull();
        (Guid taskId, _) = await SeedPublishedAsync(cts.Token);
        string id = DomainId.Short(taskId);

        (await RunCliAsync(cts.Token, "task", "queue", id)).Should().BeNull();

        TaskDetails held = await TaskAsync(taskId, cts.Token);
        held.State.Should().Be(TaskState.Blocked);
        held.FeedbackStepHeld.Should().BeTrue();
        foreach (string[] claim in new[]
                 {
                     new[] { "task", "start", id },
                     new[] { "task", "start", id, "--acknowledge-unmet-dependencies" },
                     new[] { "task", "drive", id },
                     new[] { "task", "drive", id, "--acknowledge-unmet-dependencies" },
                 })
        {
            Exception? refused = await RunCliAsync(cts.Token, claim);
            refused.Should().BeOfType<DomainConflictException>(string.Join(' ', claim)).Which.Message.Should().Contain("held for its feedback step");
        }

        // The surfaces say it is held, and the override is refused with the reason.
        (await RunCliAsync(cts.Token, "task", "show", id)).Should().BeNull();
        (await RunCliAsync(cts.Token, "status")).Should().BeNull();
        TaskShowCommandRows(held).Should().Contain(row => row.Label == "Held");
        (await RunCliAsync(cts.Token, "task", "revise", id, "--feedback-step", "off"))
            .Should().BeOfType<DomainConflictException>().Which.Message.Should().Contain("held for its feedback step");

        // A person dequeuing it ends the hold, and it can be queued again, though the step has not begun so it is held again.
        (await RunCliAsync(cts.Token, "task", "dequeue", id)).Should().BeNull();
        (await TaskAsync(taskId, cts.Token)).FeedbackStepHeld.Should().BeFalse();
    }

    private static IReadOnlyList<(string Label, string Markup)> TaskShowCommandRows(TaskDetails details) =>
        Hall9k.Cli.Commands.TaskShowCommand.FeedbackStepRows(details, new ProjectDetails { TakesFeedbackStep = true });

    [Fact]
    public async Task A_parked_step_and_a_released_one_are_each_printed_by_task_show_and_a_parked_one_is_listed_under_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid blockerId) = await SeedPublishedAsync(cts.Token);
        TaskAggregate task = (await Aggregate(taskId, cts.Token))!;
        TaskDependency closedOut = new(blockerId, "The blocker", TaskState.Done, true, RunState.Completed, null, TaskType.Feature, []);
        TaskAssigned assigned = TaskDecider.Assign(
            task, _me.OwnerId, [closedOut], Now, _me.OwnerId, null, default, projectTakesFeedbackStep: true);
        await Append(taskId, assigned, cts.Token);
        task = (await Aggregate(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Blocked);
        await Append(taskId, FeedbackStepDecider.Start(task, _me.NodeId, Now), cts.Token);
        task = (await Aggregate(taskId, cts.Token))!;
        (TaskUnassigned dequeued, TaskReturnedToDraft drafted, FeedbackStepRefinementBegun begun) =
            FeedbackStepDecider.TakeBackToDraft(task, DomainId.New(), _me.OwnerId, Now);
        await Append(taskId, dequeued, cts.Token);
        await Append(taskId, drafted, cts.Token);
        await Append(taskId, begun, cts.Token);
        task = (await Aggregate(taskId, cts.Token))!;
        await Append(taskId, FeedbackStepDecider.Conclude(task, FeedbackStepOutcome.Parked, "a challenge said do not publish on the last round the plan may run", Now), cts.Token);

        TaskDetails details = await TaskAsync(taskId, cts.Token);
        details.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.Parked);
        Hall9k.Cli.Commands.TaskShowCommand.FeedbackStepRows(details, new ProjectDetails()).Should()
            .Contain(row => row.Label == "Feedback outcome" && row.Markup.Contains("parked for a person") && row.Markup.Contains("do not publish"));
        (await RunCliAsync(cts.Token, "task", "show", DomainId.Short(taskId))).Should().BeNull();
        (await RunCliAsync(cts.Token, "status")).Should().BeNull();

        await using IQuerySession query = _postgres.Store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        row.FeedbackStepOutcome.Should().Be(FeedbackStepOutcome.Parked);
        row.FeedbackStepOutcomeReason.Should().Contain("do not publish");
    }

    [Fact]
    public void A_parked_step_whose_task_the_plan_already_published_points_at_queueing_it_rather_than_revising_it()
    {
        Guid taskId = DomainId.New();
        string id = DomainId.Short(taskId);
        TaskDetails details = new()
        {
            Id = taskId,
            State = TaskState.Published,
            FeedbackStepBegun = true,
            FeedbackStepResult = new FeedbackStepOutcomeRecorded(taskId, FeedbackStepOutcome.Parked, "queueing it again was refused", Now),
        };

        (string _, string markup) = TaskShowCommandRows(details).Single(row => row.Label == "Feedback outcome");

        markup.Should().Contain($"It is already published; settle it by hand: queue it (h9k task queue {id}), or abandon it.");
        markup.Should().NotContain("h9k task revise").And.NotContain("h9k task publish");
    }

    [Fact]
    public async Task A_plan_that_carries_a_feedback_step_is_shown_with_its_cap_of_one_challenge_round_and_the_task_it_belongs_to()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, _) = await SeedPublishedAsync(cts.Token);
        Guid planId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<Hall9k.Domain.Features.Plan.PlanAggregate>(
                planId,
                new Hall9k.Domain.Features.Plan.Events.PlanStarted(
                    planId, _projectId, [taskId], Hall9k.Domain.Features.Plan.PlanPhase.Refinement, false, false, _me.OwnerId, Now,
                    FeedbackStepTaskId: taskId));
            await session.SaveChangesAsync(cts.Token);
        }

        (await RunCliAsync(cts.Token, "plan", "show", DomainId.Short(planId))).Should().BeNull();
        (await RunCliAsync(cts.Token, "plan", "list")).Should().BeNull();

        await using IDocumentSession end = _postgres.Store.LightweightSession();
        end.Events.Append(planId, new Hall9k.Domain.Features.Plan.Events.PlanCancelled(planId, _projectId, "test over", null, Now));
        await end.SaveChangesAsync(cts.Token);
    }

    private async Task<TaskAggregate?> Aggregate(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
    }

    private async Task Append(Guid taskId, object @event, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.Append(taskId, @event);
        await session.SaveChangesAsync(cancellationToken);
    }
}
