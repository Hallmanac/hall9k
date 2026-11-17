using FluentAssertions;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Orchestrator;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k task drive --print-prompt</c> names the project's current binding on a re-entry, not the account the
/// run froze when it was claimed: the pasted session registers the account it runs under, and the next drive
/// offers a resume only when that equals the binding. The command opens its own store, so this drives the
/// account read its re-entry path makes (<see cref="TaskDriveCommand.ProjectAccountAsync"/>) and the handoff that
/// prints it, against a real store holding a claimed task whose run froze the old binding.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PrintPromptAccountRebindTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;

    public PrintPromptAccountRebindTests(PostgresFixture postgres) => this.postgres = postgres;

    [Fact]
    public async Task A_re_entry_after_the_project_was_rebound_names_the_new_binding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        string root = Path.Join(Path.GetTempPath(), $"h9k-rebind-{DomainId.New():N}");
        string oldDirectory = Path.Join(root, ".claude-old");
        string newDirectory = Path.Join(root, ".claude-new");
        Guid oldConnection = await ClaudeCodeBindingSeed.RegisterConnectionAsync(store, oldDirectory, Now, cts.Token);
        Guid newConnection = await ClaudeCodeBindingSeed.RegisterConnectionAsync(store, newDirectory, Now, cts.Token);

        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"rebind-{projectId:N}", Path.Join(root, "repo"), null, "main", Now);
            seed.Events.StartStream<ProjectAggregate>(projectId, registered);

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Carry the account on the handoff", ["it names the account"], TaskType.Chore,
                null, null, null, Now, node.OwnerId);
            TaskAggregate task = new();
            task.Apply(added);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, node.OwnerId);
            task.Apply(published);
            (TaskAssigned assigned, TaskClaimed claimed, _) = TaskDriveCommand.PrepareInteractiveClaimFromPublished(
                task, node.OwnerId, [], runId, Now, acknowledgeUnmetDependencies: false);
            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, claimed);

            // The account the claim froze on its run: the binding at the time, which a re-entry's spawn would use.
            seed.Events.StartStream<RunAggregate>(runId, new RunClaudeCodeAccountRecorded(runId, oldConnection, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await ClaudeCodeBindingSeed.BindAsync(store, projectId, oldConnection, Now, cts.Token);
        await ClaudeCodeBindingSeed.BindAsync(store, projectId, newConnection, Now.AddMinutes(5), cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate reentered = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        reentered.State.Should().Be(TaskState.Claimed);
        reentered.IsInteractiveClaim.Should().BeTrue("this is the claim a second h9k task drive re-enters");
        RunDetails run = (await session.LoadAsync<RunDetails>(runId, cts.Token))!;
        (await CliClaudeAccount.ForRunAsync(session, run, cts.Token)).ConfigDirectory.Should().Be(
            oldDirectory, "the run froze the binding it was claimed under");

        ClaudeCodeAccount account = await TaskDriveCommand.ProjectAccountAsync(session, reentered, cts.Token);
        string output = ScopedAnsiConsoleCapture.Capture(() => TaskDriveCommand.PrintPromptHandoff(
            taskId, "/w", "task/b", "the prompt", "/runs/r/settings.json", isFreshInteractiveClaim: false, account,
            LaunchShell.Posix));

        output.Should().Contain($"bound Claude Code account, an unobserved account ({newDirectory})")
            .And.Contain($"CLAUDE_CONFIG_DIR='{newDirectory}' claude --settings \"/runs/r/settings.json\"")
            .And.NotContain(oldDirectory);
    }
}
