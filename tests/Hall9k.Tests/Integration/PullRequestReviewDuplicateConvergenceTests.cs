using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// One GitHub review request, one live pr-review task across every node of the owner's fleet.
/// <para>
/// The incident these pin (arx-platform 2140, 2143 and 2147): two daemons of one owner each minted
/// a task for the same review request within seconds, because each sees the other's task only when
/// replication lands, minutes later, after both dispatchers have claimed. Every test here uses two
/// real stores exchanging events through the replication seam (<see cref="ReplicatedFleet"/>), and
/// picks its task ids by hand, because the survivor is decided by id and never by a clock: the
/// survivor in most of these was minted <em>later</em> by its own node's wall clock.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PullRequestReviewDuplicateConvergenceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PullRequest = "acme/widgets#42";

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 36, 10, TimeSpan.Zero);

    // UUIDv7 leads with a millisecond timestamp, so Smaller is the earlier mint by id.
    private static readonly Guid Smaller = Guid.Parse("01997a00-0000-7000-8000-0000000000a1");
    private static readonly Guid Larger = Guid.Parse("01997a00-0001-7000-8000-0000000000b2");

    private Task<ReplicatedFleet> StartFleetAsync(CancellationToken cancellationToken) =>
        ReplicatedFleet.StartAsync(postgres, "duplicate_convergence_node_b", cleanPrimary: true, cancellationToken);

    [Fact]
    public async Task Both_nodes_minting_for_one_request_end_with_one_live_task_and_the_same_survivor_after_the_exchange_is_replayed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);

        // The survivor's own node minted it later by its wall clock: the clocks disagree, and the
        // ids must still name one survivor on both sides.
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now.AddSeconds(30), cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Larger, Now, cts.Token);

        await fleet.ExchangeBothWaysAsync(cts.Token);
        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);
        (await fleet.B.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        // Replayed in both directions, twice: the abandons cross, and nothing resurrects or doubles.
        await fleet.ExchangeBothWaysAsync(cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);
        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(0);
        (await fleet.B.Convergence.ConvergeAsync(cts.Token)).Should().Be(0);

        foreach (ReplicatedPeer peer in new[] { fleet.A, fleet.B })
        {
            (await ReplicatedFleet.LiveReviewsAsync(peer, PullRequest, cts.Token))
                .Should().Equal([Smaller], $"{peer.Name} must hold exactly one live review, the smaller id");
            (await ReplicatedFleet.ItemAsync(peer, Larger, cts.Token)).State.Should().Be(TaskState.Abandoned);
            (await ReplicatedFleet.ItemAsync(peer, Smaller, cts.Token)).State.Should().Be(TaskState.Queued);
        }
    }

    [Fact]
    public async Task The_abandon_is_recorded_as_the_owner_names_the_survivor_and_shows_once_per_node_in_the_courier_feed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Larger, Now, cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);
        await fleet.ExchangeAsync(fleet.A, fleet.B, cts.Token);
        (await fleet.B.Convergence.ConvergeAsync(cts.Token)).Should().Be(0, "the abandon already arrived, so there is nothing left to decide");

        foreach (ReplicatedPeer peer in new[] { fleet.A, fleet.B })
        {
            await using IQuerySession query = peer.Store.QuerySession();
            IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(Larger, token: cts.Token);
            TaskAbandoned abandoned = stream.Select(recorded => recorded.Data).OfType<TaskAbandoned>()
                .Should().ContainSingle($"{peer.Name}'s feed shows the abandon once").Subject;

            abandoned.AbandonedByOwnerId.Should().Be(fleet.A.Node.OwnerId, "the daemon abandons as the owner");
            OrchestratorFeedDescription.Of(abandoned).Should().Be(
                $"abandoned: {PullRequestReviewDuplicateRule.AbandonReason(Smaller)}");
            OrchestratorFeedDescription.Of(abandoned).Should().Contain(Smaller.ToString());
        }
    }

    [Fact]
    public async Task A_replicated_younger_duplicate_arriving_after_the_local_one_is_claimed_is_refused_by_the_dispatcher_and_then_abandoned()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        Guid unrelated = Guid.Parse("01997a00-0002-7000-8000-0000000000c3");
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await fleet.ClaimAsync(fleet.A, Smaller, cts.Token);
        await fleet.SeedReviewAsync(fleet.A, unrelated, Now, cts.Token, pullRequest: "acme/widgets#43");
        await fleet.SeedReviewAsync(fleet.B, Larger, Now, cts.Token);
        await fleet.ExchangeAsync(fleet.B, fleet.A, cts.Token);

        // Before the convergence pass has run at all: the dispatcher's own refusal is the guarantee.
        IReadOnlyList<ClaimedWork> claimed = await NewDispatcher(fleet.A).ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Contain(unrelated, "the dispatcher was live and claiming");
        claimed.Select(work => work.TaskId).Should().NotContain(Larger, "an older live twin of the same owner holds this request");
        (await ReplicatedFleet.ItemAsync(fleet.A, Larger, cts.Token)).State.Should().Be(TaskState.Queued);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);
        (await ReplicatedFleet.ItemAsync(fleet.A, Larger, cts.Token)).State.Should().Be(TaskState.Abandoned);
        (await ReplicatedFleet.ItemAsync(fleet.A, Smaller, cts.Token)).State.Should().Be(TaskState.Claimed);
    }

    [Fact]
    public async Task A_younger_twin_is_refused_even_when_the_older_one_is_only_queued_on_a_node_that_never_claims_it()
    {
        // The incident's aggravator: the older twin sat queued under a per-project ceiling of 0 on
        // its own node. The younger one must still not run on another node meanwhile.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Smaller, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now.AddSeconds(-30), cts.Token);
        await fleet.ExchangeAsync(fleet.B, fleet.A, cts.Token);

        IReadOnlyList<ClaimedWork> claimed = await NewDispatcher(fleet.A).ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().NotContain(Larger);
        (await ReplicatedFleet.ItemAsync(fleet.A, Larger, cts.Token)).State.Should().Be(TaskState.Queued);
    }

    [Fact]
    public async Task The_local_younger_task_is_abandoned_when_the_older_one_replicates_in_and_gives_back_its_lease_and_holder()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now, cts.Token);
        await fleet.ClaimAsync(fleet.A, Larger, cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Smaller, Now.AddSeconds(30), cts.Token);
        await fleet.ExchangeAsync(fleet.B, fleet.A, cts.Token);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        await AssertGivenBackAsync(fleet.A, Larger, cts.Token);
        (await ReplicatedFleet.ItemAsync(fleet.A, Smaller, cts.Token)).State.Should().Be(TaskState.Queued, "the survivor is untouched");
    }

    [Fact]
    public async Task An_abandon_that_arrives_by_replication_still_gives_back_the_lease_and_holder_this_node_holds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Smaller, Now, cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);

        // B decides first and abandons A's twin; A meanwhile claimed it, not yet knowing.
        (await fleet.B.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);
        await fleet.ClaimAsync(fleet.A, Larger, cts.Token);
        await fleet.ExchangeAsync(fleet.B, fleet.A, cts.Token);

        (await ReplicatedFleet.ItemAsync(fleet.A, Larger, cts.Token)).State.Should().Be(TaskState.Abandoned);
        await using (IQuerySession query = fleet.A.Store.QuerySession())
        {
            (await query.LoadAsync<TaskLease>(Larger, cts.Token)).Should().NotBeNull("nothing has released it yet");
        }

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(0, "it was not A's abandon to make");

        await AssertGivenBackAsync(fleet.A, Larger, cts.Token);
    }

    [Fact]
    public async Task An_abandon_that_arrives_by_replication_still_releases_the_holder_after_the_finished_run_deleted_its_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Smaller, Now, cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);

        // B decides first. A claimed its twin, and the review run finished: the engine deletes the
        // lease at finalization and leaves the ledger holder naming A.
        (await fleet.B.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);
        await fleet.ClaimAsync(fleet.A, Larger, cts.Token);
        await using (IDocumentSession session = fleet.A.Store.LightweightSession())
        {
            session.Delete<TaskLease>(Larger);
            await session.SaveChangesAsync(cts.Token);
        }

        await fleet.ExchangeAsync(fleet.B, fleet.A, cts.Token);
        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(0, "it was not A's abandon to make");

        await AssertGivenBackAsync(fleet.A, Larger, cts.Token);
    }

    [Fact]
    public async Task A_replicated_claim_on_the_loser_arriving_after_its_abandon_is_reconverged_on_the_next_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Larger, Now, cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);
        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        // The origin node, not yet aware, claims its own twin. The claim applies unconditionally.
        await fleet.ClaimAsync(fleet.B, Larger, cts.Token);
        await fleet.ExchangeAsync(fleet.B, fleet.A, cts.Token);
        (await ReplicatedFleet.ItemAsync(fleet.A, Larger, cts.Token)).State.Should().Be(
            TaskState.Claimed, "this is the resurrection the standing pass exists to undo");

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        (await ReplicatedFleet.ItemAsync(fleet.A, Larger, cts.Token)).State.Should().Be(TaskState.Abandoned);
        await using IQuerySession query = fleet.A.Store.QuerySession();
        IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(Larger, token: cts.Token);
        stream.Select(recorded => recorded.Data).OfType<TaskHolderReleased>()
            .Should().BeEmpty("the holder was the origin node's, not this one's to release");
    }

    [Fact]
    public async Task A_teammates_live_review_of_the_same_pull_request_is_never_abandoned_and_never_blocks_a_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        Guid teammate = DomainId.New();
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token, assignedTo: teammate);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now.AddSeconds(5), cts.Token);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(0);
        IReadOnlyList<ClaimedWork> claimed = await NewDispatcher(fleet.A).ClaimEligibleAsync(cts.Token);

        (await ReplicatedFleet.LiveReviewsAsync(fleet.A, PullRequest, cts.Token)).Should().BeEquivalentTo([Smaller, Larger]);
        claimed.Select(work => work.TaskId).Should().Contain(Larger, "the teammate's older task is not this owner's twin");
    }

    [Fact]
    public async Task A_task_a_person_adopted_by_hand_is_never_abandoned_by_the_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now, cts.Token, autoCreated: false);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(0);

        (await ReplicatedFleet.LiveReviewsAsync(fleet.A, PullRequest, cts.Token)).Should().BeEquivalentTo([Smaller, Larger]);
    }

    [Fact]
    public async Task A_mention_recorded_on_the_loser_reaches_the_survivor()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Larger, Now, cts.Token);
        await AppendMentionAsync(fleet.B, Larger, "IC_first", Now.AddMinutes(1), cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        await using IQuerySession query = fleet.A.Store.QuerySession();
        TaskAggregate survivor = (await query.Events.AggregateStreamAsync<TaskAggregate>(Smaller, token: cts.Token))!;
        survivor.LatestMentionCommentId.Should().Be("IC_first");
        survivor.LatestMentionAuthorLogin.Should().Be("carol");
        survivor.State.Should().Be(TaskState.Queued, "carrying a mention does not touch the survivor's state");
    }

    [Fact]
    public async Task A_mention_older_than_the_survivors_own_latest_is_left_on_the_loser_rather_than_moving_the_latest_backwards()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await AppendMentionAsync(fleet.A, Smaller, "IC_newer", Now.AddMinutes(5), cts.Token);
        await fleet.SeedReviewAsync(fleet.B, Larger, Now, cts.Token);
        await AppendMentionAsync(fleet.B, Larger, "IC_older", Now.AddMinutes(1), cts.Token);
        await fleet.ExchangeBothWaysAsync(cts.Token);

        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        await using IQuerySession query = fleet.A.Store.QuerySession();
        TaskAggregate survivor = (await query.Events.AggregateStreamAsync<TaskAggregate>(Smaller, token: cts.Token))!;
        survivor.LatestMentionCommentId.Should().Be("IC_newer");
    }

    [Fact]
    public async Task Status_shows_the_survivor_as_the_covering_task_and_no_needs_you_row_for_the_duplicate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        Guid statusProjectId = DomainId.New();
        await using (IDocumentSession session = fleet.A.Store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                statusProjectId, fleet.A.Node.OwnerId, DomainId.New(), "duplicate-status", "/tmp/duplicate-status-repo",
                new Uri("https://github.com/acme/widgets"), "main", Now.AddDays(-1));
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
            session.Store(new ObservedReviewRequest
            {
                Id = ObservedReviewRequest.ComputeId(fleet.A.Node.NodeId, statusProjectId, "acme/widgets", 42, "brian"),
                ObservingNodeId = fleet.A.Node.NodeId,
                ProjectId = statusProjectId,
                Repository = "acme/widgets",
                Number = 42,
                PullRequestUrl = "https://github.com/acme/widgets/pull/42",
                ReviewerLogin = "brian",
                RequestedAt = Now,
                FirstObservedAt = Now,
                LastObservedAt = Now,
                Outcome = ReviewRequestOutcome.TaskCreated,
                TaskId = Larger,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await fleet.SeedReviewAsync(fleet.A, Smaller, Now, cts.Token);
        await fleet.SeedReviewAsync(fleet.A, Larger, Now, cts.Token);
        (await fleet.A.Convergence.ConvergeAsync(cts.Token)).Should().Be(1);

        await using IQuerySession query = fleet.A.Store.QuerySession();
        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(query, Now.AddMinutes(10), cts.Token);
        ReviewRequestPaneContents pane = await ReviewRequestPane.ComposeAllAsync(query, rows, Now.AddMinutes(10), cts.Token);

        ReviewRequestRow row = pane.Requests.Should().ContainSingle().Subject;
        row.NeedsYou.Should().BeFalse("a duplicate that was abandoned is nothing for the operator to act on");
        row.Markup.Should().Contain($"task {DomainId.Short(Smaller)} is created and reviewing");
        row.Markup.Should().NotContain(DomainId.Short(Larger));
        TaskStatusRow duplicate = rows.Single(status => status.TaskId == Larger);
        duplicate.State.Should().Be(LifecycleState.Archived);
        duplicate.Group.Should().NotBe(AttentionBucket.NeedsYou, "the duplicate is archived, never a needs-you row");
    }

    private DispatchEngine NewDispatcher(ReplicatedPeer peer) => new(
        peer.Store, peer.Node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
        new LaunchHoldEngine(peer.Store, NullLogger<LaunchHoldEngine>.Instance),
        Options.Create(new DaemonOptions { MaxConcurrentTaskRuns = 5 }), NullLogger<DispatchEngine>.Instance);

    /// <summary>A twin this node held: its lease is deleted, the holder release is on the stream, and the ledger release is owed for the dispatcher's own pending sweep to confirm.</summary>
    private static async Task AssertGivenBackAsync(ReplicatedPeer peer, Guid taskId, CancellationToken cancellationToken)
    {
        (await ReplicatedFleet.ItemAsync(peer, taskId, cancellationToken)).State.Should().Be(TaskState.Abandoned);

        await using IQuerySession query = peer.Store.QuerySession();
        (await query.LoadAsync<TaskLease>(taskId, cancellationToken)).Should().BeNull("an abandoned task keeps no lease");

        IReadOnlyList<IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cancellationToken);
        stream.Select(recorded => recorded.Data).OfType<TaskHolderReleased>().Should().ContainSingle();

        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        task.HolderNodeId.Should().BeNull();

        TaskHolderReleasePending? owed = await query.LoadAsync<TaskHolderReleasePending>(
            TaskHolderReleasePending.KeyFor(taskId, peer.Node.NodeId), cancellationToken);
        owed.Should().NotBeNull("the ledger record still names this node until the pending release lands");
    }

    private static async Task AppendMentionAsync(
        ReplicatedPeer peer, Guid taskId, string commentId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = peer.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.ObservePrReviewMention(
            task, "https://github.com/acme/widgets/pull/42", commentId, "carol", "@brian can you look",
            $"https://github.com/acme/widgets/pull/42#{commentId}", at, at));
        await session.SaveChangesAsync(cancellationToken);
    }
}
