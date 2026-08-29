using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class PullRequestOpenerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = SetTempHome();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-pr-{Guid.NewGuid():N}");

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-prhome-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task Local_origin_flow_pushes_branch_completes_task_and_removes_worktree()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        // Real repo with a local bare origin, real worktree, and a real "agent" commit.
        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# pr test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, runId, "Open a PR end to end"), cts.Token);

        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "agent output\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");

        // Seed task (claimed) + run (verified) pointing at the worktree.
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Open a PR end to end", ["branch lands on origin"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = claimed.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(runId, taskId, claimed.NodeId, ownerId, 1, DomainId.New(),
                    worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(runId, Now),
                new VerificationPassed(runId, Now));

            // The opener needs the project row for repository path + base branch.
            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(runId, taskId, cts.Token);

        // Branch is on origin, task is Done without a PR, lease gone, worktree removed.
        (int exitCode, string output) = TryGit(originPath, $"rev-parse --verify refs/heads/{worktree.Branch}");
        exitCode.Should().Be(0, $"the branch must be pushed to origin (output: {output})");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Done");
        task2.PullRequestUrl.Should().BeNull("a non-GitHub origin gets no PR");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull();
        Directory.Exists(worktree.Path).Should().BeTrue(
            "the worktree is retained through closeout — it IS the follow-up workspace (log #21)");
    }

    /// <summary>
    /// The generation fence (backlog 39): a requeue-and-reclaim moved the task on to
    /// generation 2 under a fresh run while this run — still generation 1 — reached the
    /// push step. The origin incident's exact shape: a stale lane's push must not complete
    /// the task the live generation still owns, nor take that generation's lease with it.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_push_does_not_complete_the_live_generations_task_or_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# fence test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid staleRunId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, staleRunId, "Stale generation push"), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "stale run output\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid liveNodeId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Stale generation push", ["never completes as generation 1"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var staleClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, staleRunId, Now);
            task.Apply(staleClaim);
            // A requeue-and-reclaim moved the task on to generation 2 under a different run
            // while this run's push was already in flight — exactly the double-booking shape.
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var liveClaim = TaskDecider.Claim(task, liveNodeId, ownerId, DomainId.New(), Now);
            task.Apply(liveClaim);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, staleClaim, requeued, liveClaim]);
            session.Store(new TaskLease { Id = taskId, NodeId = liveNodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(staleRunId,
                new RunDispatched(staleRunId, taskId, staleClaim.NodeId, ownerId, 1, DomainId.New(),
                    worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(staleRunId, Now),
                new VerificationPassed(staleRunId, Now));

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        ListLogger<PullRequestOpener> logger = new();
        PullRequestOpener opener = new(store, logger);
        await opener.OpenAsync(staleRunId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim survives the stale run's push");
        task2.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's push must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));
    }

    [Fact]
    public async Task Follow_up_flow_pushes_the_existing_branch_and_updates_the_pull_request_in_place()
    {
        const string pullRequestUrl = "https://github.com/x/y/pull/7";
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# follow-up test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        // First run's lifecycle: branch created, work pushed, worktree removed.
        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Worktree first = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Follow up end to end"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");
        Git(first.Path, $"push -q origin {first.Branch}");
        await worktrees.RemoveAsync(repoPath, first.Path, cts.Token);

        // Follow-up run: reopened task claimed at generation 2, agent committed a fix on
        // the checked-out existing branch, gates passed.
        Guid followUpRunId = DomainId.New();
        Worktree followUp = await worktrees.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(repoPath, first.Branch, taskId, followUpRunId), cts.Token);
        File.WriteAllText(Path.Combine(followUp.Path, "FIX.md"), "review feedback resolved\n");
        Git(followUp.Path, "add -A");
        Git(followUp.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Resolve review feedback\"");

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Follow up end to end", ["review comments resolved"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var firstClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, firstRunId, Now);
            task.Apply(firstClaim);
            var completed = TaskDecider.Complete(task, firstRunId, pullRequestUrl, Now);
            task.Apply(completed);
            var reopened = TaskDecider.Reopen(
                task, firstRunId, first.Branch, "Unresolved review comments",
                FollowUpKind.ReviewFeedback, automatic: false, Now, ownerId);
            task.Apply(reopened);
            var followUpClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, followUpRunId, Now);
            task.Apply(followUpClaim);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, firstClaim, completed, reopened, followUpClaim]);
            session.Store(new TaskLease { Id = taskId, NodeId = followUpClaim.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(followUpRunId,
                new RunDispatched(followUpRunId, taskId, followUpClaim.NodeId, ownerId, 2, DomainId.New(),
                    followUp.Path, followUp.Branch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(followUpRunId, Now),
                new VerificationPassed(followUpRunId, Now));

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(followUpRunId, taskId, cts.Token);

        // The fix landed on the SAME branch on origin; no second PR, same URL on the task.
        (int exitCode, string output) = TryGit(originPath, $"show {first.Branch}:FIX.md");
        exitCode.Should().Be(0, $"the follow-up commit must be pushed to the existing branch (output: {output})");

        await using IQuerySession query = store.QuerySession();
        TaskListItem taskView = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        taskView.State.Value.Should().Be("Done");
        taskView.PullRequestUrl.Should().Be(pullRequestUrl, "the follow-up completes with the ORIGINAL PR URL");

        Hall9k.Domain.Features.Run.Projections.RunDetails runView =
            (await query.LoadAsync<Hall9k.Domain.Features.Run.Projections.RunDetails>(followUpRunId, cts.Token))!;
        runView.State.Value.Should().Be("AwaitingReview", "PullRequestUpdated parks the follow-up run awaiting review");
        runView.PullRequestUrl.Should().Be(pullRequestUrl);
        runView.PullRequestNumber.Should().Be(7);

        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull();
        Directory.Exists(followUp.Path).Should().BeTrue(
            "follow-up worktrees are retained like first-run ones until closeout completes (log #21)");
    }

    [Fact]
    public async Task Follow_up_with_rewritten_history_force_pushes_the_rebased_branch()
    {
        const string pullRequestUrl = "https://github.com/x/y/pull/9";
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# force-push test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Force push follow up"), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "first run\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");
        Git(worktree.Path, $"push -q origin {worktree.Branch}");

        // The follow-up agent folded its fix into the owning commit (narrative style):
        // the amended tip DIVERGES from origin — a plain push is rejected here, which is
        // the 2026-08-17 stranded-work incident this path fixes.
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "first run, review fix folded in\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -q --amend -m \"Add WORK.md, absorbed\"");

        Guid followUpRunId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Force push follow up", ["fix folded into owning commit"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var firstClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, firstRunId, Now);
            task.Apply(firstClaim);
            var completed = TaskDecider.Complete(task, firstRunId, pullRequestUrl, Now);
            task.Apply(completed);
            var reopened = TaskDecider.Reopen(
                task, firstRunId, worktree.Branch, "Unresolved review comments",
                FollowUpKind.ReviewFeedback, automatic: false, Now, ownerId);
            task.Apply(reopened);
            var followUpClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, followUpRunId, Now);
            task.Apply(followUpClaim);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, firstClaim, completed, reopened, followUpClaim]);
            session.Store(new TaskLease { Id = taskId, NodeId = followUpClaim.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(followUpRunId,
                new RunDispatched(followUpRunId, taskId, followUpClaim.NodeId, ownerId, 2, DomainId.New(),
                    worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now, IsFollowUp: true),
                new AgentSessionCompleted(followUpRunId, Now),
                new VerificationPassed(followUpRunId, Now));

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(followUpRunId, taskId, cts.Token);

        // The rewritten tip landed on origin (force-with-lease), and the run flowed
        // through the normal follow-up pipeline instead of failing at the push.
        (int exitCode, string remoteMessage) = TryGit(originPath, $"log -1 --format=%s {worktree.Branch}");
        exitCode.Should().Be(0);
        remoteMessage.Trim().Should().Be("Add WORK.md, absorbed", "origin must hold the rebased history");
        (_, string localTip) = TryGit(worktree.Path, "rev-parse HEAD");
        (_, string remoteTip) = TryGit(originPath, $"rev-parse {worktree.Branch}");
        remoteTip.Trim().Should().Be(localTip.Trim());

        await using IQuerySession query = store.QuerySession();
        TaskListItem taskView = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        taskView.State.Value.Should().Be("Done", "the force-pushed follow-up completes like any other");
        taskView.PullRequestUrl.Should().Be(pullRequestUrl);

        Hall9k.Domain.Features.Run.Projections.RunDetails runView =
            (await query.LoadAsync<Hall9k.Domain.Features.Run.Projections.RunDetails>(followUpRunId, cts.Token))!;
        runView.State.Value.Should().Be("AwaitingReview",
            "PullRequestUpdated appends and the closeout monitor's next sweep watches the new tip");
        runView.PullRequestNumber.Should().Be(9);
    }

    /// <summary>
    /// Task: build sessions stop stranding finished work uncommitted. Every push is now
    /// --force-with-lease, not only a follow-up's — a fresh <c>Build</c> session's own
    /// end-of-work checkpoint recompose can diverge a retried run's branch from a tip this
    /// same opener already pushed once (push succeeded, `gh pr create` then failed). The
    /// sibling test above (<see cref="Follow_up_with_rewritten_history_force_pushes_the_rebased_branch"/>)
    /// covers exactly this shape for <c>IsFollowUp: true</c>; this covers the non-follow-up
    /// arm the review found untested (cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_retried_non_follow_up_run_with_a_diverged_recompose_still_lands_via_force_with_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# recompose retry test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Recompose retry lands via lease"), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "checkpoint\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm checkpoint");
        Git(worktree.Path, $"push -q origin {worktree.Branch}");

        // Run 2 resumes the SAME worktree after run 1's push landed but the run still failed
        // (the exact shape: push succeeded, `gh pr create` then failed) and follows the
        // checkpoint-recompose protocol: reset to the fork point, then compose fresh history
        // over it. The new tip shares no ancestry with the tip already on origin.
        Git(worktree.Path, "reset --mixed HEAD~1");
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "recomposed\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm recomposed");

        Guid secondRunId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Recompose retry lands via lease", ["branch lands despite divergence"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var firstClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, firstRunId, Now);
            task.Apply(firstClaim);
            var failed = TaskDecider.Fail(task, firstRunId, "PR opening failed: gh pr create failed", Now);
            task.Apply(failed);
            var retried = TaskDecider.Retry(task, firstRunId, worktree.Branch, "retry after PR creation failure", Now, ownerId);
            task.Apply(retried);
            var secondClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, secondRunId, Now);
            task.Apply(secondClaim);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, firstClaim, failed, retried, secondClaim]);
            session.Store(new TaskLease { Id = taskId, NodeId = secondClaim.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(firstRunId, taskId, firstClaim.NodeId, ownerId, 1, DomainId.New(),
                    worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(firstRunId, Now),
                new VerificationPassed(firstRunId, Now),
                new RunFailed(firstRunId, "PR opening failed: gh pr create failed", Now));

            session.Events.StartStream<RunAggregate>(secondRunId,
                new RunDispatched(secondRunId, taskId, secondClaim.NodeId, ownerId, 2, DomainId.New(),
                    worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(secondRunId, Now),
                new VerificationPassed(secondRunId, Now));

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(secondRunId, taskId, cts.Token);

        // The diverged, recomposed tip landed on origin despite sharing no ancestry with the
        // tip run 1 already pushed there — a plain push would have been rejected outright.
        (int exitCode, string remoteMessage) = TryGit(originPath, $"log -1 --format=%s {worktree.Branch}");
        exitCode.Should().Be(0);
        remoteMessage.Trim().Should().Be("recomposed", "the recomposed tip must have landed via force-with-lease");
        (_, string localTip) = TryGit(worktree.Path, "rev-parse HEAD");
        (_, string remoteTip) = TryGit(originPath, $"rev-parse {worktree.Branch}");
        remoteTip.Trim().Should().Be(localTip.Trim());

        await using IQuerySession query = store.QuerySession();
        TaskListItem taskView = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        taskView.State.Value.Should().Be("Done", "the retried run completes once its diverged tip lands");
    }

    private static void Git(string workingDirectory, string arguments)
    {
        (int exitCode, string output) = TryGit(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    private static (int ExitCode, string Output) TryGit(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        foreach (string dir in new[] { _home, _root })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
