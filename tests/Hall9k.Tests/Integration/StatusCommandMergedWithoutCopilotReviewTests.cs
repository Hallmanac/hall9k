using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k status</c> names a pull request that merged while Copilot's review sat unavailable
/// (task: a Copilot review refused for quota is treated as review unavailable) — the acceptance
/// criterion the task itself named: "h9k status shows the pull request as merged without Copilot
/// review when that happened". A task that merged this way is Done and earns no row of its own in
/// the pane's per-task sections (Decisions Log #66: the pane is what needs you, not a browse
/// surface), so <see cref="StatusCommand.WriteMergedWithoutCopilotReviewAsync"/> is the one place
/// left that still says so, off the durable <see cref="RunDetails"/> fields a real task and run
/// event stream leaves behind (Brian's 2026-09-13 testing rule: a real Marten/Postgres store, no
/// repository, no remote) — fresh domain ids per test are what isolate them, the same as every
/// other class sharing this fixture's one container (<see cref="PostgresFixture"/>'s own doc
/// comment).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class StatusCommandMergedWithoutCopilotReviewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MergedAt = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private const string PullRequestUrl = "https://github.com/example/repo/pull/408";

    [Fact]
    public async Task A_task_merged_with_Copilot_review_unavailable_is_named_by_task_and_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedMergedTaskAsync("Ship the refusal path", 408, PullRequestUrl, merged: true, cts.Token);

        string output;
        await using (IQuerySession session = postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteMergedWithoutCopilotReviewAsync(session, cts.Token));
        }

        output.Should().Contain("merged without Copilot review")
            .And.Contain("Ship the refusal path")
            .And.Contain("PR #408");
    }

    /// <summary>
    /// A refusal that never merged (the ordinary Delivered case, or a non-pre-approved task still
    /// waiting on the human's own merge) must not appear here — this line is scoped to the merge
    /// actually having happened, which is the one fact the task's own AC named, not every refusal
    /// this install has ever observed.
    /// </summary>
    [Fact]
    public async Task A_refusal_recorded_on_a_run_that_has_not_merged_renders_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedMergedTaskAsync(
            "Still watching the pull request", 409, "https://github.com/example/repo/pull/409",
            merged: false, cts.Token);

        string output;
        await using (IQuerySession session = postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteMergedWithoutCopilotReviewAsync(session, cts.Token));
        }

        output.Should().NotContain("PR #409", "the pull request has not merged yet, so there is nothing this line owes a reader about it");
    }

    /// <summary>
    /// The daemon's own pre-approved auto-merge (<c>CloseoutEngine.TryAutoMergeAsync</c>) always
    /// appends <c>PullRequestMerged</c> with a null <c>MergedAt</c> — nothing re-reads GitHub's own
    /// merge timestamp after the merge call. This is the exact case the task's own acceptance
    /// criterion named (a pre-approved pull request merging on the remaining gates after a quota
    /// refusal), so it must render exactly like a merge that did carry GitHub's own timestamp
    /// (independent pre-PR review, cycle 3, both lenses, high).
    /// </summary>
    [Fact]
    public async Task A_pre_approved_auto_merge_with_no_GitHub_timestamp_still_renders()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await SeedAutoMergedTaskAsync(
            "Auto-merge on the remaining gates", 410, "https://github.com/example/repo/pull/410", cts.Token);

        string output;
        await using (IQuerySession session = postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteMergedWithoutCopilotReviewAsync(session, cts.Token));
        }

        output.Should().Contain("merged without Copilot review")
            .And.Contain("Auto-merge on the remaining gates")
            .And.Contain("PR #410");
    }

    /// <summary>
    /// A refusal recorded, then a real Copilot review landing before the merge, must not render —
    /// Copilot did in fact review the pull request before it merged, so "merged without Copilot
    /// review" would be false (independent pre-PR review, cycle 3, adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task A_refusal_superseded_by_a_landed_review_before_merging_renders_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();
        const int pullRequestNumber = 411;
        const string pullRequestUrl = "https://github.com/example/repo/pull/411";

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Copilot came back before the merge", ["merged"], TaskType.Chore, null, null, null,
            Now, ownerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, runId, Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, runId, pullRequestUrl, MergedAt);
        task.Apply(completed);

        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, completed]);

        const string reason = "Copilot was unable to review this pull request because the user who "
            + "requested the review has reached their quota limit.";
        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, nodeId, ownerId, 1, DomainId.New(), "/does/not/matter", "task/branch",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now),
            new PullRequestOpened(runId, pullRequestUrl, pullRequestNumber, Now),
            new CopilotReviewUnavailable(
                runId, "copilot-pull-request-reviewer", $"{pullRequestUrl}#pullrequestreview-1", reason, Now),
            new ExternalReviewObserved(
                runId, ExternalReviewState.Landed, 0, false, MergedAt.AddMinutes(-10), "APPROVED"),
            new PullRequestMerged(runId, MergedAt, MergedAt),
            new RunCompleted(runId, MergedAt));
        await session.SaveChangesAsync(cts.Token);

        string output;
        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteMergedWithoutCopilotReviewAsync(query, cts.Token));
        }

        output.Should().NotContain("PR #411", "Copilot's review landed before the merge, so the merge did not happen without it");
    }

    /// <summary>
    /// The run that observed the refusal need not be the run whose own merge landed — a
    /// review-feedback follow-up run can be the one that actually merges. The line must still find
    /// this task by reading across every one of its runs (independent pre-PR review, cycle 3,
    /// conformance lens, medium).
    /// </summary>
    [Fact]
    public async Task A_refusal_on_one_run_and_a_merge_on_a_later_run_of_the_same_task_still_renders()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid secondRunId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();
        const int pullRequestNumber = 412;
        const string pullRequestUrl = "https://github.com/example/repo/pull/412";

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "A follow-up run finishes the merge", ["merged"], TaskType.Chore, null, null, null,
            Now, ownerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, firstRunId, Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, secondRunId, pullRequestUrl, MergedAt);
        task.Apply(completed);

        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, completed]);

        const string reason = "Copilot was unable to review this pull request because the user who "
            + "requested the review has reached their quota limit.";
        session.Events.StartStream<RunAggregate>(
            firstRunId,
            new RunDispatched(
                firstRunId, taskId, nodeId, ownerId, 1, DomainId.New(), "/does/not/matter", "task/branch",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(firstRunId, Now),
            new VerificationPassed(firstRunId, Now),
            new PullRequestOpened(firstRunId, pullRequestUrl, pullRequestNumber, Now),
            new CopilotReviewUnavailable(
                firstRunId, "copilot-pull-request-reviewer", $"{pullRequestUrl}#pullrequestreview-1", reason, Now));
        session.Events.StartStream<RunAggregate>(
            secondRunId,
            new RunDispatched(
                secondRunId, taskId, nodeId, ownerId, 1, DomainId.New(), "/does/not/matter", "task/branch",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(secondRunId, Now),
            new VerificationPassed(secondRunId, Now),
            new PullRequestMerged(secondRunId, MergedAt, MergedAt),
            new RunCompleted(secondRunId, MergedAt));
        await session.SaveChangesAsync(cts.Token);

        string output;
        await using (IQuerySession query = postgres.Store.QuerySession())
        {
            output = await CaptureAsync(() => StatusCommand.WriteMergedWithoutCopilotReviewAsync(query, cts.Token));
        }

        output.Should().Contain("merged without Copilot review")
            .And.Contain("A follow-up run finishes the merge")
            .And.Contain("PR #412");
    }

    /// <summary>
    /// Seeds a task and run identically to the merged branch of <see cref="SeedMergedTaskAsync"/>,
    /// except the merge event carries a null <c>MergedAt</c> — exactly what
    /// <c>CloseoutEngine.TryAutoMergeAsync</c> actually appends, rather than the non-null timestamp
    /// the hand-built merge case above uses.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedAutoMergedTaskAsync(
        string objective, int pullRequestNumber, string pullRequestUrl, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, objective, ["merged"], TaskType.Chore, null, null, null, Now, ownerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);

        TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, runId, Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, runId, pullRequestUrl, MergedAt);
        task.Apply(completed);

        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, completed]);

        const string reason = "Copilot was unable to review this pull request because the user who "
            + "requested the review has reached their quota limit.";
        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, nodeId, ownerId, 1, DomainId.New(), "/does/not/matter", "task/branch",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now),
            new PullRequestOpened(runId, pullRequestUrl, pullRequestNumber, Now),
            new CopilotReviewUnavailable(
                runId, "copilot-pull-request-reviewer", $"{pullRequestUrl}#pullrequestreview-1", reason, Now),
            new PullRequestMerged(runId, null, MergedAt),
            new RunCompleted(runId, MergedAt));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }

    /// <summary>
    /// Builds a task through the same lifecycle the CLI walks a human through (<see cref="TaskSeed"/>)
    /// and a run through the pipeline's own events, ending either in a real merge plus the quota
    /// refusal (<paramref name="merged"/> true) or with the refusal recorded but no merge yet.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedMergedTaskAsync(
        string objective, int pullRequestNumber, string pullRequestUrl, bool merged, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid nodeId = DomainId.New();

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, objective, ["merged"], TaskType.Chore, null, null, null, Now, ownerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, ownerId, Now);

        TaskClaimed claimed = TaskDecider.Claim(task, nodeId, ownerId, runId, Now);
        task.Apply(claimed);

        object[] taskEvents = [.. lifecycle, claimed];
        if (merged)
        {
            TaskCompleted completed = TaskDecider.Complete(task, runId, pullRequestUrl, MergedAt);
            task.Apply(completed);
            taskEvents = [.. taskEvents, completed];
        }

        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, taskEvents);

        const string reason = "Copilot was unable to review this pull request because the user who "
            + "requested the review has reached their quota limit.";
        List<object> runEvents =
        [
            new RunDispatched(
                runId, taskId, nodeId, ownerId, 1, DomainId.New(), "/does/not/matter", "task/branch",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now),
            new PullRequestOpened(runId, pullRequestUrl, pullRequestNumber, Now),
            new CopilotReviewUnavailable(
                runId, "copilot-pull-request-reviewer", $"{pullRequestUrl}#pullrequestreview-1", reason, Now),
        ];
        if (merged)
        {
            runEvents.Add(new PullRequestMerged(runId, MergedAt, MergedAt));
            runEvents.Add(new RunCompleted(runId, MergedAt));
        }

        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }

    /// <summary>The global console, swapped for a writer and put back — mirrors LaunchLineWrappingTests's own capture.</summary>
    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 4096;
        AnsiConsole.Console = captured;
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}
