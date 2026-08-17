using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Worktrees;
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
            var added = TaskDecider.Add(taskId, projectId, "Open a PR end to end", ["branch lands on origin"],
                TaskType.Chore, null, null, null, Now, ownerId);
            task.Apply(added);
            var claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, added, claimed);
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
            var added = TaskDecider.Add(taskId, projectId, "Follow up end to end", ["review comments resolved"],
                TaskType.Chore, null, null, null, Now, ownerId);
            task.Apply(added);
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
            session.Events.StartStream<TaskAggregate>(taskId, added, firstClaim, completed, reopened, followUpClaim);
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
