using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
        DocumentStore store = postgres.Store;

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
            new WorktreeRequest(repoPath, "main", taskId, runId, "Open a PR end to end", BranchNameTemplate.Default, ExternalReference: null), cts.Token);

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
        DocumentStore store = postgres.Store;

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
            new WorktreeRequest(repoPath, "main", taskId, staleRunId, "Stale generation push", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
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
        DocumentStore store = postgres.Store;

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
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Follow up end to end", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
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
        DocumentStore store = postgres.Store;

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
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Force push follow up", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
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
    /// Task: build sessions stop stranding finished work uncommitted. Every push was already
    /// --force-with-lease unconditionally before this task (decision #103's <c>h9k task deliver</c>
    /// change made <c>IsFollowUp</c> an unreliable proxy for "does a remote copy already exist") —
    /// a fresh <c>Build</c> session's own end-of-work checkpoint recompose can diverge a retried
    /// run's branch from a tip this same opener already pushed once (push succeeded, `gh pr
    /// create` then failed). The
    /// sibling test above (<see cref="Follow_up_with_rewritten_history_force_pushes_the_rebased_branch"/>)
    /// covers exactly this shape for <c>IsFollowUp: true</c>; this covers the non-follow-up
    /// arm the review found untested (cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_retried_non_follow_up_run_with_a_diverged_recompose_still_lands_via_force_with_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

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
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Recompose retry lands via lease", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
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

    /// <summary>
    /// Adversarial review, cycle 3: a stale local <c>refs/remotes/origin/&lt;branch&gt;</c>
    /// tracking ref for a branch origin no longer has (deleted externally — by hand, or by
    /// another node's <c>DeleteBranchEverywhereAsync</c> — after this node last accounted
    /// for it) must not turn a recoverable "origin has nothing for this branch" case into a
    /// hard run failure. <see cref="GitWorktreeManager"/>'s best-effort fetch never runs
    /// with <c>--prune</c>, so the stale tracking ref survives locally with no fetch of this
    /// node's own to correct it before the push.
    /// </summary>
    [Fact]
    public async Task A_stale_tracking_ref_for_a_branch_origin_no_longer_has_still_lands_via_force_with_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# stale tracking ref test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, firstRunId, "Stale tracking ref still lands", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "checkpoint\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm checkpoint");
        // Run 1's push lands (and, via git's opportunistic update, refreshes this worktree's
        // own refs/remotes/origin/<branch> tracking ref) but `gh pr create` then fails.
        Git(worktree.Path, $"push -q origin {worktree.Branch}");

        // The branch is deleted from origin by something other than this worktree's own
        // push (an operator via GitHub's UI, or another node's cleanup) — this worktree
        // never fetches, so its tracking ref still reads the old, now-nonexistent tip.
        Git(originPath, $"branch -D {worktree.Branch}");

        Guid secondRunId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Stale tracking ref still lands", ["branch lands despite the stale tracking ref"],
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

        // The branch must have been recreated on origin despite the stale local tracking
        // ref, which would have rejected a lease pinned to the deleted, remembered tip.
        (int exitCode, string output) = TryGit(originPath, $"rev-parse --verify refs/heads/{worktree.Branch}");
        exitCode.Should().Be(0, $"the branch must land on origin despite the stale tracking ref (output: {output})");
        (_, string localTip) = TryGit(worktree.Path, "rev-parse HEAD");
        (_, string remoteTip) = TryGit(originPath, $"rev-parse {worktree.Branch}");
        remoteTip.Trim().Should().Be(localTip.Trim());

        await using IQuerySession query = store.QuerySession();
        TaskListItem taskView = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        taskView.State.Value.Should().Be("Done", "the retried run completes despite the stale tracking ref");
    }

    /// <summary>
    /// The refusal branch that is the entire protective value of the cycle-1 fix
    /// (independent pre-PR review, cycle 2, conformance lens): nothing exercised a foreign
    /// tip on origin causing <see cref="PullRequestOpener"/>'s push guard to throw, so a
    /// later refactor that inverted the safety condition or dropped the reflog fallback
    /// could reintroduce the silent overwrite cycle 1 fixed while the suite stayed green.
    /// A second clone pushes a commit this worktree never fetched into HEAD or its own
    /// reflog, reproducing the exact scenario the cycle-1 fix guards against.
    /// </summary>
    [Fact]
    public async Task A_foreign_tip_on_origin_refuses_the_push_and_leaves_origin_untouched()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# foreign tip test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, runId, "Foreign tip refuses the push", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "agent output\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");
        Git(worktree.Path, $"push -q origin {worktree.Branch}");
        (_, string originalTip) = TryGit(originPath, $"rev-parse {worktree.Branch}");

        // A second clone pushes a commit onto the same branch that this worktree never
        // incorporates into HEAD or its own reflog.
        string foreignClonePath = Path.Combine(_root, "foreign-clone");
        Git(_root, $"clone \"{originPath}\" \"{foreignClonePath}\"");
        Git(foreignClonePath, $"checkout -q {worktree.Branch}");
        File.WriteAllText(Path.Combine(foreignClonePath, "FOREIGN.md"), "someone else's commit\n");
        Git(foreignClonePath, "add -A");
        Git(foreignClonePath, "-c user.name=Other -c user.email=o@o commit -qm \"Foreign commit\"");
        Git(foreignClonePath, $"push -q origin {worktree.Branch}");

        // The worktree fetches (as BestEffortFetchAsync would on the real pull side),
        // refreshing its remote-tracking ref to the foreign tip without touching local HEAD
        // or the branch's own reflog — the exact shape the cycle-1 fix guards against.
        Git(worktree.Path, "fetch -q origin");
        (_, string foreignTip) = TryGit(originPath, $"rev-parse {worktree.Branch}");

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Foreign tip refuses the push", ["origin's foreign tip survives"],
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

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(runId, taskId, cts.Token);

        // Origin's foreign tip must survive untouched — the guard refused rather than
        // force-overwriting it.
        (_, string originTipAfter) = TryGit(originPath, $"rev-parse {worktree.Branch}");
        originTipAfter.Trim().Should().Be(foreignTip.Trim(),
            "the refused push must leave the foreign tip on origin exactly as it was");
        originTipAfter.Trim().Should().NotBe(originalTip.Trim());

        await using IQuerySession query = store.QuerySession();
        TaskListItem taskView = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        taskView.State.Value.Should().Be("Failed", "a refused push fails the run honestly rather than retrying blindly");

        Hall9k.Domain.Features.Run.Projections.RunDetails runView =
            (await query.LoadAsync<Hall9k.Domain.Features.Run.Projections.RunDetails>(runId, cts.Token))!;
        runView.State.Value.Should().Be("Failed");
        runView.FailureReason.Should().Contain(
            "someone else moved the branch", "the guard's own refusal message names what happened");
    }

    /// <summary>
    /// The other nonzero-exit arm of the push guard (independent pre-PR review, cycle 1,
    /// conformance lens): only <c>tipExit == 2</c> (origin has no such ref) and the refusal
    /// branch had a test, leaving the <c>tipExit != 0</c> case (origin unreadable, distinct
    /// from origin-having-nothing) unpinned. A collapse of the two nonzero exits back into
    /// one, treating any <c>ls-remote</c> failure as "origin has nothing," would push with
    /// <c>--force-with-lease=&lt;branch&gt;:</c> against an unreachable remote instead of
    /// failing the run honestly. Origin is made unreachable by deleting the bare repository
    /// out from under the worktree's already-configured remote, after the worktree is created
    /// (so creation's own fetch still succeeds), reproducing a remote that answers neither
    /// "here is the tip" nor "no such ref" but simply cannot be read.
    /// </summary>
    [Fact]
    public async Task An_unreadable_origin_at_push_time_fails_the_run_rather_than_guessing_it_has_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# unreadable origin test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, runId, "Unreadable origin fails the run", BranchNameTemplate.Default, ExternalReference: null), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "agent output\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");

        // Origin is gone by the time the push guard's ls-remote runs: unreachable, not
        // merely empty of this ref. Through TemporaryTree because git leaves its own loose
        // objects read-only, which Directory.Delete refuses outright on Windows.
        TemporaryTree.Delete(originPath);

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Unreadable origin fails the run", ["origin unreachable fails honestly"],
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

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cts.Token);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem taskView = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        taskView.State.Value.Should().Be("Failed", "an unreadable origin fails the run honestly rather than guessing it has nothing");

        Hall9k.Domain.Features.Run.Projections.RunDetails runView =
            (await query.LoadAsync<Hall9k.Domain.Features.Run.Projections.RunDetails>(runId, cts.Token))!;
        runView.State.Value.Should().Be("Failed");
        runView.FailureReason.Should().Contain(
            "could not read origin's current tip", "the guard's own refusal message names what happened");
    }

    /// <summary>
    /// The wiring, as opposed to the composition <c>PrSummaryArtifactTests</c> pins as a pure
    /// function: that the opener actually reads the run directory's <c>pr-summary.md</c> and puts
    /// both halves of what it finds into what <c>gh</c> is told — the authored title as
    /// <c>--title</c>, the authored prose into the <c>--body-file</c> it names. Nothing else in
    /// this class can catch that read going away, because every other test here runs against a
    /// local origin, where no pull request is opened at all (independent pre-PR review, cycle 1,
    /// conformance lens: deleting the artifact read left every pull request reopening on the
    /// skeleton with the whole suite green).
    /// </summary>
    [Fact]
    public async Task What_gh_is_told_comes_from_the_run_directorys_pull_request_summary()
    {
        string runDirectory = Path.Combine(_root, "run-that-composed-one");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(
            RunPaths.PrSummaryFile(runDirectory),
            "Title: Resolve references in every host\n\nEvery host uses the shared provider now.");

        IReadOnlyList<string> arguments = await PullRequestOpener.CreateArgumentsAsync(
            NullLogger.Instance, ComposingRun(runDirectory), ComposingTask(), "run narration", sourceUrl: null,
            "main", WritingConventions.Default, CancellationToken.None);

        arguments.Should().Equal(
            "pr", "create",
            "--title", "ARX-4861: Resolve references in every host",
            "--body-file", Path.Combine(runDirectory, "pr-body.md"),
            "--base", "main",
            "--head", "task/12345678-resolve-references");
        (await File.ReadAllTextAsync(Path.Combine(runDirectory, "pr-body.md")))
            .Should().Contain("Every host uses the shared provider now.")
            .And.NotContain("run narration", "a session that composed a body already said what a reviewer needs");
    }

    /// <summary>
    /// The other half of the same wiring: a run whose session composed nothing — an interactive
    /// claim delivered by hand, a session killed before its final message — still opens on the
    /// skeleton the daemon has always written, with the key-prefixed short title as the one thing
    /// that improves.
    /// </summary>
    [Fact]
    public async Task What_gh_is_told_falls_back_to_the_skeleton_when_no_session_composed_one()
    {
        string runDirectory = Path.Combine(_root, "run-that-composed-none");
        Directory.CreateDirectory(runDirectory);

        IReadOnlyList<string> arguments = await PullRequestOpener.CreateArgumentsAsync(
            NullLogger.Instance, ComposingRun(runDirectory), ComposingTask(), "run narration", sourceUrl: null,
            "main", WritingConventions.Default, CancellationToken.None);

        File.Exists(RunPaths.PrSummaryFile(runDirectory)).Should().BeFalse("no session composed one");
        arguments.Should().ContainInOrder(
            "--title", "ARX-4861: Turn an external work item into a task with one command");
        (await File.ReadAllTextAsync(Path.Combine(runDirectory, "pr-body.md")))
            .Should().Contain("## Acceptance criteria")
            .And.Contain("run narration", "with no authored body the run's own narration is what there is");
    }

    private static Hall9k.Domain.Features.Run.Projections.RunDetails ComposingRun(string runDirectory) => new()
    {
        Id = DomainId.New(),
        TaskId = DomainId.New(),
        RunDirectory = runDirectory,
        Branch = "task/12345678-resolve-references",
    };

    private static TaskDetails ComposingTask() => new()
    {
        Id = DomainId.New(),
        Objective = "Turn an external work item into a task with one command",
        AcceptanceCriteria = ["The importer refuses a closed issue"],
        ExternalReference = "jira:ARX-4861",
    };

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
            TemporaryTree.TryDelete(dir);
        }
    }

    /// <summary>
    /// Records what <see cref="PullRequestOpener"/> asked the provider for after the push. Every
    /// read throws: this opener only ever writes through the inspector, so a read reaching here
    /// would be a silent new dependency rather than a passing test.
    /// </summary>
    private sealed class RecordingRerequestInspector(bool refuse = false) : Hall9k.Daemon.Closeout.IPullRequestInspector
    {
        public List<Hall9k.Daemon.Closeout.PullRequestReviewer> Rerequested { get; } = [];

        public Task<Hall9k.Daemon.Closeout.PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the opener never reads a snapshot");

        public Task<Hall9k.Daemon.Closeout.PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the opener never reads a state snapshot");

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber,
            Hall9k.Daemon.Closeout.PullRequestReviewer reviewer, CancellationToken cancellationToken)
        {
            if (refuse)
            {
                return Task.FromException(new InvalidOperationException("HTTP 422: Reviews may only be requested from collaborators."));
            }

            Rerequested.Add(reviewer);
            return Task.CompletedTask;
        }

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken) => throw new NotSupportedException("the opener never merges");

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken) => throw new NotSupportedException("the opener never retargets");
    }

    /// <summary>
    /// The lap has pushed, so the person who blocked the pull request is asked to look at the new
    /// head (task: a changes-requested pull-request review from a human becomes a fix lap). The
    /// login is recorded on the run too, which is what keeps closeout's human-engagement check
    /// from reading the platform's own request back as a human's.
    /// </summary>
    [Fact]
    public async Task A_changes_requested_lap_rerequests_the_reviewer_once_it_has_pushed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        RecordingRerequestInspector inspector = new();
        (Guid taskId, Guid runId, DocumentStore store) =
            await RunChangesRequestedFollowUpAsync(inspector, cts.Token);

        Hall9k.Daemon.Closeout.PullRequestReviewer asked =
            inspector.Rerequested.Should().ContainSingle().Subject;
        asked.Login.Should().Be("teammate");
        asked.Kind.Should().Be(
            Hall9k.Daemon.Closeout.ReviewerKind.Human,
            "a human login is never [bot]-suffixed by the re-request call");

        await using IQuerySession query = store.QuerySession();
        Hall9k.Domain.Features.Run.Projections.RunDetails run =
            (await query.LoadAsync<Hall9k.Domain.Features.Run.Projections.RunDetails>(runId, cts.Token))!;
        run.RequestedReviewerLogins.Should().Equal("teammate");
        run.ReviewRerequestsAfterFixes.Should().Be(
            0, "this is not the opt-in countersign and must not spend its pass cap");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be(
            "Done", "the delivery is complete either way — the re-request is the last thing, not a gate on it");
    }

    /// <summary>
    /// A refused re-request costs the request and not the delivery: the reviewer's own
    /// changes-requested verdict still stands on GitHub, so nothing merges past them, and recording
    /// a request the provider rejected would corrupt closeout's own human-engagement comparison.
    /// </summary>
    [Fact]
    public async Task A_refused_rerequest_still_completes_the_task_and_records_no_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        (Guid taskId, Guid runId, DocumentStore store) =
            await RunChangesRequestedFollowUpAsync(new RecordingRerequestInspector(refuse: true), cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Done");
        (await query.LoadAsync<Hall9k.Domain.Features.Run.Projections.RunDetails>(runId, cts.Token))!
            .RequestedReviewerLogins.Should().BeEmpty(
                "a request nobody accepted is never written as though it had been asked");
    }

    /// <summary>
    /// The follow-up lifecycle of <see cref="Follow_up_flow_pushes_the_existing_branch_and_updates_the_pull_request_in_place"/>,
    /// reopened as a changes-requested lap instead of a thread lap, run through the opener.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, DocumentStore Store)> RunChangesRequestedFollowUpAsync(
        Hall9k.Daemon.Closeout.IPullRequestInspector inspector, CancellationToken cancellationToken)
    {
        const string pullRequestUrl = "https://github.com/x/y/pull/11";
        DocumentStore store = postgres.Store;

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, "origin.git");
        string repoPath = Path.Combine(_root, "repo");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# changes-requested test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Worktree first = await worktrees.CreateAsync(
            new WorktreeRequest(
                repoPath, "main", taskId, firstRunId, "Bound the limiter", BranchNameTemplate.Default,
                ExternalReference: null),
            cancellationToken);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Add WORK.md\"");
        Git(first.Path, $"push -q origin {first.Branch}");
        await worktrees.RemoveAsync(repoPath, first.Path, cancellationToken);

        Guid followUpRunId = DomainId.New();
        Worktree followUp = await worktrees.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(repoPath, first.Branch, taskId, followUpRunId), cancellationToken);
        File.WriteAllText(Path.Combine(followUp.Path, "FIX.md"), "the limiter resets per window\n");
        Git(followUp.Path, "add -A");
        Git(followUp.Path, "-c user.name=Test -c user.email=t@t commit -qm \"Answer the review\"");

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Bound the limiter", ["the limiter resets per window"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            var firstClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, firstRunId, Now);
            task.Apply(firstClaim);
            var completed = TaskDecider.Complete(task, firstRunId, pullRequestUrl, Now);
            task.Apply(completed);
            var reopened = TaskDecider.Reopen(
                task, firstRunId, first.Branch, "@teammate requested changes.",
                FollowUpKind.ReviewRequestedChanges, automatic: true, Now, ownerId,
                changesRequestedReviews:
                [
                    new Hall9k.Domain.Features.Run.ChangesRequestedReview(
                        "teammate", $"{pullRequestUrl}#pullrequestreview-42", Now,
                        [new Hall9k.Domain.Features.Run.ChangesRequestedFinding(
                            "This limiter never resets.", "src/Limiter.cs:42", "PRRT_abc")]),
                ]);
            task.Apply(reopened);
            var followUpClaim = TaskDecider.Claim(task, DomainId.New(), ownerId, followUpRunId, Now);
            task.Apply(followUpClaim);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, firstClaim, completed, reopened, followUpClaim]);
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = followUpClaim.NodeId, LeaseGeneration = 2, HeartbeatAt = Now,
            });

            session.Events.StartStream<RunAggregate>(followUpRunId,
                new RunDispatched(followUpRunId, taskId, followUpClaim.NodeId, ownerId, 2, DomainId.New(),
                    followUp.Path, followUp.Branch, ExecutorMode.Subscription, Now, IsFollowUp: true),
                new AgentSessionCompleted(followUpRunId, Now),
                new VerificationPassed(followUpRunId, Now));

            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, ownerId, DomainId.New(), $"pr-{taskId:N}", repoPath, null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);
            await session.SaveChangesAsync(cancellationToken);
        }

        PullRequestOpener opener = new(store, NullLogger<PullRequestOpener>.Instance, inspector);
        await opener.OpenAsync(followUpRunId, taskId, cancellationToken);
        return (taskId, followUpRunId, store);
    }
}
