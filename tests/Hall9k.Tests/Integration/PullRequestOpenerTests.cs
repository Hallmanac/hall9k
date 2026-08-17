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

        PullRequestOpener opener = new(store, worktrees, NullLogger<PullRequestOpener>.Instance);
        await opener.OpenAsync(runId, taskId, cts.Token);

        // Branch is on origin, task is Done without a PR, lease gone, worktree removed.
        (int exitCode, string output) = TryGit(originPath, $"rev-parse --verify refs/heads/{worktree.Branch}");
        exitCode.Should().Be(0, $"the branch must be pushed to origin (output: {output})");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Done");
        task2.PullRequestUrl.Should().BeNull("a non-GitHub origin gets no PR");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull();
        Directory.Exists(worktree.Path).Should().BeFalse("done worktrees are removed (branch is safe on origin)");
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
