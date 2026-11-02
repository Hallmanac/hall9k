using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What <c>h9k doctor</c>'s courier section reads off a real store: the registered windows on this
/// machine's node, their streaks, and the one line it prints when there are none.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class CourierDoctorReadTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly PostgresFixture _postgres;

    public CourierDoctorReadTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task With_no_node_or_no_registered_window_the_section_says_so_in_one_line()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        FakeOrchestratorProcessProbe probe = new();

        IReadOnlyList<string> noNode = await CourierDoctor.ReadAsync(_postgres.Store, probe, Now, cts.Token);
        noNode.Should().Equal(CourierDoctor.NoWindowLine);

        Guid nodeId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeId, new NodeRegistered(nodeId, DomainId.New(), Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<string> noWindow = await CourierDoctor.ReadAsync(_postgres.Store, probe, Now, cts.Token);
        noWindow.Should().Equal(CourierDoctor.NoWindowLine);
    }

    [Fact]
    public async Task A_registered_window_with_a_long_streak_warns_and_a_quiet_one_gets_an_ok_line()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid failingProject = DomainId.New();
        Guid quietProject = DomainId.New();
        DateTimeOffset launchedAt = Now.AddMinutes(-20);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeId, new NodeRegistered(nodeId, ownerId, Environment.MachineName, "Windows", Now));
            int processId = 41_000;
            foreach ((Guid projectId, string name) in new[] { (failingProject, "failing"), (quietProject, "quiet") })
            {
                session.Events.StartStream<ProjectAggregate>(
                    projectId, ProjectDecider.Register(projectId, ownerId, DomainId.New(), name, "/repo", null, null, Now));
                session.Events.StartStream<OrchestratorPresenceAggregate>(
                    OrchestratorPresenceStreamId.For(nodeId, projectId),
                    new OrchestratorLaunched(nodeId, projectId, "orchestrator", processId++, "claude-code", launchedAt, launchedAt));
            }

            session.Store(new CourierFailureStreak
            {
                Id = failingProject,
                Count = 7,
                StartedAt = Now.AddHours(-1),
                LastFailureAt = Now.AddMinutes(-2),
                LastFailureReason = "no session by that name",
                NextAttemptAt = Now.AddMinutes(16),
            });
            await session.SaveChangesAsync(cts.Token);
        }

        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(41_000, launchedAt).Running(41_001, launchedAt);
        IReadOnlyList<string> lines = await CourierDoctor.ReadAsync(_postgres.Store, probe, Now, cts.Token);

        lines.Should().HaveCount(2);
        lines.Should().ContainSingle(line => line.Contains("warn:") && line.Contains("failing: courier failing, 7 in a row")
            && line.Contains("last: no session by that name"));
        lines.Should().ContainSingle(line => line.Contains("quiet: courier ok"));
    }

    [Fact]
    public async Task An_old_item_behind_a_full_scan_of_unrelated_events_still_warns_and_the_cursor_stays_put()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        DateTimeOffset launchedAt = Now.AddMinutes(-20);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeId, new NodeRegistered(nodeId, ownerId, Environment.MachineName, "Windows", Now));
            session.Events.StartStream<ProjectAggregate>(
                projectId, ProjectDecider.Register(projectId, ownerId, DomainId.New(), "buried", "/repo", null, null, Now));
            session.Events.StartStream<OrchestratorPresenceAggregate>(
                OrchestratorPresenceStreamId.For(nodeId, projectId),
                new OrchestratorLaunched(nodeId, projectId, "orchestrator", 42_000, "claude-code", launchedAt, launchedAt));
            await session.SaveChangesAsync(cts.Token);
        }

        // More unrelated events than one scan reads, all ahead of the project's only feed item.
        await using (IDocumentSession noise = _postgres.Store.LightweightSession())
        {
            for (int i = 0; i < OrchestratorFeedReader.MaxEventsPerRead + 100; i++)
            {
                Guid otherProject = DomainId.New();
                noise.Events.StartStream<MessageAggregate>(
                    MessageStreamId.ForMessage(DomainId.New(), otherProject, 1),
                    new MessageReceived(
                        DomainId.New(), 1, Now, "abcdef0123456789", "project", null,
                        MessageKind.Note.Value, "elsewhere", Now, otherProject));
            }

            await noise.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<MessageAggregate>(
                MessageStreamId.ForMessage(DomainId.New(), projectId, 1),
                new MessageReceived(
                    DomainId.New(), 1, Now, "abcdef0123456789", "project", null,
                    MessageKind.Note.Value, "still waiting", Now, projectId));
            await session.SaveChangesAsync(cts.Token);
        }

        // Read two hours on, so the item the store just stamped is well over an hour old.
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(42_000, launchedAt);
        IReadOnlyList<string> lines = await CourierDoctor.ReadAsync(_postgres.Store, probe, Now.AddHours(2), cts.Token);

        lines.Should().ContainSingle().Which.Should().Contain("warn:").And.Contain("a feed item has sat undrained");
        await using IQuerySession query = _postgres.Store.QuerySession();
        (await OrchestratorFeedReader.CursorAsync(query, projectId, cts.Token)).Should().Be(OrchestratorFeedCursor.NeverDrained);
    }
}
