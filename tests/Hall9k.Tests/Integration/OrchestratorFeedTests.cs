using FluentAssertions;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The orchestrator feed against a real event store (idea 89471598, piece 2) — what the unit
/// tier cannot answer: that the scan really is one query Marten can translate, that a stream is
/// really resolved to its project and task, and that a drain really survives into the next
/// command's own session.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class OrchestratorFeedTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The clock a read is handed here: a minute ahead of the real one, so the events these tests
    /// have just written count as settled and a drain may move past them. A read given the true
    /// present would hold its drain frontier back over every one of them, which is
    /// <see cref="OrchestratorFeedSelection.SettlingWindow"/> doing exactly its job — the rule
    /// itself is pinned in the unit tier, where the clock is a parameter rather than a wait.
    /// </summary>
    private static DateTimeOffset PastTheSettlingWindow => DateTimeOffset.UtcNow.AddMinutes(1);

    private readonly PostgresFixture _postgres;

    public OrchestratorFeedTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_drain_takes_the_undrained_items_and_the_next_read_finds_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(ownerId, cts.Token);
        Guid taskId = await SeedTaskAsync(projectId, ownerId, cts.Token);

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            OrchestratorFeedRead first = await reader.ReadUndrainedAsync(
                session, projectId, OrchestratorFeedLevel.Transitions, PastTheSettlingWindow, cts.Token);

            first.Items.Should().NotBeEmpty();
            first.Items.Should().OnlyContain(item => item.TaskId == taskId);
            first.Items.Select(item => item.Description).Should().Contain("published and ready to assign");

            // A second read with no drain in between is the same read.
            OrchestratorFeedRead again = await reader.ReadUndrainedAsync(
                session, projectId, OrchestratorFeedLevel.Transitions, PastTheSettlingWindow, cts.Token);
            again.Items.Should().BeEquivalentTo(first.Items, options => options.WithStrictOrdering());

            await OrchestratorFeedReader.DrainAsync(
                session, projectId, first.DrainableThroughSequence, Now, cts.Token);
        }

        // A fresh session, the way the next h9k invocation would see it.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            OrchestratorFeedRead afterDrain = await reader.ReadUndrainedAsync(
                session, projectId, OrchestratorFeedLevel.Transitions, PastTheSettlingWindow, cts.Token);

            afterDrain.Items.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_read_whose_clock_predates_the_events_prints_them_and_drains_past_none()
    {
        // The unit tier pins the settling rule against hand-made candidates; this pins the wiring
        // the rule depends on, that the instant it compares is the event's own stored timestamp.
        // Reading with a clock from before these events were written makes every one of them
        // unsettled, whatever the real time is when the suite runs.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(ownerId, cts.Token);
        await SeedTaskAsync(projectId, ownerId, cts.Token);

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        OrchestratorFeedRead read = await reader.ReadUndrainedAsync(
            session, projectId, OrchestratorFeedLevel.Transitions, Now, cts.Token);

        read.Items.Should().NotBeEmpty("an item too new to drain past is still news worth printing");
        read.DrainableThroughSequence.Should().Be(
            OrchestratorFeedCursor.NeverDrained,
            "a drain must not pass an event a lower, still-uncommitted sequence could be hiding behind");
    }

    [Fact]
    public async Task The_actionable_band_leaves_a_plain_transition_out_and_keeps_the_park()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(ownerId, cts.Token);
        Guid taskId = await SeedTaskAsync(projectId, ownerId, cts.Token);
        await SeedParkedRunAsync(taskId, ownerId, cts.Token);

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        OrchestratorFeedRead actionable = await reader.ReadUndrainedAsync(
            session, projectId, OrchestratorFeedLevel.Actionable, PastTheSettlingWindow, cts.Token);
        OrchestratorFeedRead everything = await reader.ReadUndrainedAsync(
            session, projectId, OrchestratorFeedLevel.Everything, PastTheSettlingWindow, cts.Token);

        actionable.Items.Select(item => item.Description).Should()
            .ContainSingle().Which.Should().StartWith("the review loop parked for a human:");
        everything.Items.Count.Should().BeGreaterThan(actionable.Items.Count);

        // A run's own events group under the task that owns the run, not under the run.
        actionable.Items.Should().OnlyContain(item => item.TaskId == taskId);
    }

    [Fact]
    public async Task A_since_read_never_moves_the_cursor()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(ownerId, cts.Token);
        await SeedTaskAsync(projectId, ownerId, cts.Token);

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        OrchestratorFeedRead history = await reader.ReadSinceAsync(
            session, projectId, OrchestratorFeedLevel.Transitions,
            DateTimeOffset.UtcNow.AddDays(-1), PastTheSettlingWindow, cts.Token);
        history.Items.Should().NotBeEmpty();

        (await OrchestratorFeedReader.CursorAsync(session, projectId, cts.Token))
            .Should().Be(OrchestratorFeedCursor.NeverDrained);
    }

    [Fact]
    public async Task A_since_naming_an_absolute_instant_in_the_machines_own_zone_still_queries()
    {
        // OrchestratorFeedSince.Parse reads a bare date the way a person means it, in local time,
        // so the instant it hands back carries that zone's offset — and Npgsql refuses outright
        // to bind a DateTimeOffset with a non-zero offset to a timestamptz column. Every absolute
        // --since the help text offers lands here, and UtcNow-based fixtures never do, which is
        // why this one names the offset explicitly.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(ownerId, cts.Token);
        await SeedTaskAsync(projectId, ownerId, cts.Token);

        DateTimeOffset fiveHoursBehindUtc = new(
            DateTime.UtcNow.AddDays(-1).AddHours(-5).Ticks, TimeSpan.FromHours(-5));

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        OrchestratorFeedRead history = await reader.ReadSinceAsync(
            session, projectId, OrchestratorFeedLevel.Transitions,
            fiveHoursBehindUtc, PastTheSettlingWindow, cts.Token);

        history.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Another_projects_task_never_shows_in_this_projects_feed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid mine = await SeedProjectAsync(ownerId, cts.Token);
        Guid theirs = await SeedProjectAsync(ownerId, cts.Token);
        Guid myTask = await SeedTaskAsync(mine, ownerId, cts.Token);
        await SeedTaskAsync(theirs, ownerId, cts.Token);

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        OrchestratorFeedRead read = await reader.ReadUndrainedAsync(
            session, mine, OrchestratorFeedLevel.Transitions, PastTheSettlingWindow, cts.Token);

        read.Items.Should().NotBeEmpty();
        read.Items.Should().OnlyContain(item => item.TaskId == myTask);
    }

    [Fact]
    public async Task A_message_addressed_to_this_project_is_an_item_even_at_the_narrowest_band()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(ownerId, cts.Token);
        Guid fromNodeId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<MessageAggregate>(
                MessageStreamId.ForMessage(fromNodeId, projectId, 1),
                new MessageReceived(
                    fromNodeId, 1, Now, "abcdef0123456789", "project", null,
                    MessageKind.Note.Value, "are you still on the stacked pair?", Now, projectId));
            await session.SaveChangesAsync(cts.Token);
        }

        OrchestratorFeedReader reader = new(new ReplicationProjectResolver());
        await using IDocumentSession read = _postgres.Store.LightweightSession();

        OrchestratorFeedRead feed = await reader.ReadUndrainedAsync(
            read, projectId, OrchestratorFeedLevel.Actionable, PastTheSettlingWindow, cts.Token);

        feed.Items.Should().ContainSingle()
            .Which.Description.Should().Be("a message from abcdef012345: are you still on the stacked pair?");
    }

    /// <summary>
    /// The feed courier's own manual-drain lease (idea 89471598, piece 3), round-tripped through
    /// the real store the way <c>OrchestratorFeedCommand</c>'s own <c>--drain</c> path writes and
    /// deletes it: held while a manual drain is in progress, gone once it completes, so the
    /// courier's gate never mistakes a finished drain for one still running.
    /// </summary>
    [Fact]
    public async Task A_drain_lease_is_held_until_deleted_and_gone_once_it_is()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Store(OrchestratorFeedDrainLease.Held(projectId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession read = _postgres.Store.QuerySession())
        {
            OrchestratorFeedDrainLease? lease = await read.LoadAsync<OrchestratorFeedDrainLease>(projectId, cts.Token);
            OrchestratorFeedDrainLease.IsHeld(lease, Now).Should().BeTrue();
            OrchestratorFeedDrainLease.IsHeld(lease, Now + OrchestratorFeedDrainLease.Duration).Should().BeFalse(
                "the lease's own duration is a ceiling on how long it protects a drain, not an indefinite hold");
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Delete<OrchestratorFeedDrainLease>(projectId);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession afterDelete = _postgres.Store.QuerySession();
        OrchestratorFeedDrainLease? gone = await afterDelete.LoadAsync<OrchestratorFeedDrainLease>(projectId, cts.Token);
        OrchestratorFeedDrainLease.IsHeld(gone, Now).Should().BeFalse();
    }

    private async Task<Guid> SeedProjectAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"project-{DomainId.Short(projectId)}", "/repo", null, null, Now);
        session.Events.StartStream<ProjectAggregate>(projectId, registered);
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    private async Task<Guid> SeedTaskAsync(Guid projectId, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, ownerId);
        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(taskId, new TaskPublished(taskId, Now, ownerId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<Guid> SeedParkedRunAsync(Guid taskId, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid runId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, DomainId.New(), ownerId, 1, DomainId.New(), "/worktree",
                $"task/{DomainId.Short(taskId)}-ship", ExecutorMode.Subscription, Now),
            new ReviewParked(runId, "the adversarial lens and the fix session disagree about finding 2", Now));
        await session.SaveChangesAsync(cancellationToken);
        return runId;
    }
}
