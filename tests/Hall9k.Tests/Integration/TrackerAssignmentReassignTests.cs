using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The reassignment an operator at a terminal can confirm over an item somebody else holds, on Jira
/// and GitHub against the shared <see cref="FakeTracker"/>: the offer and its decline, with and
/// without <c>--take</c> and on a task that is already queued, the fresh read before the write, the
/// read-back that decides what is recorded, and the doors that must never reassign. The offer is a
/// <see cref="TrackerClaimCheck.TakeOffer"/>, so no console is involved.
/// </summary>
public sealed partial class TrackerAssignmentTests
{
    private static readonly Guid ThisNode = new("0191f0a0-0000-7000-8000-00000000a001");
    private static readonly Guid OtherNode = new("0191f0a0-0000-7000-8000-00000000b002");

    private static string HolderFor(string provider) => provider == "jira" ? "someone-else" : "teammate";

    private static TrackerClaimCheck.ReassignDoor Door(
        TrackerClaimCheck.TakeOffer offer, Guid? holderNodeId = null) => new(offer, ThisNode, holderNodeId);

    private static TrackerClaimCheck.TakeOffer Yes(List<TrackerClaimDecision> asked) => decision =>
    {
        asked.Add(decision);
        return true;
    };

    private static TrackerClaimCheck.TakeOffer No(List<TrackerClaimDecision> asked) => decision =>
    {
        asked.Add(decision);
        return false;
    };

    /// <summary>The unassigned-item offer, which a held item must never reach.</summary>
    private static readonly TrackerClaimCheck.TakeOffer NotForAHeldItem = _ =>
        throw new InvalidOperationException("the take offer is for an unassigned item, and this one is held");

    // ── a confirmed reassignment ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_confirmed_reassignment_with_take_replaces_the_holder_and_records_it_in_place_of_the_write(
        string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-1" : "801";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        List<TrackerClaimDecision> asked = [];

        TrackerTake? take = await ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, Door(Yes(asked)), cts.Token);

        asked.Should().ContainSingle().Which.Holders.Should().ContainSingle().Which.Identity.Should().Be(HolderFor(provider));
        take!.Verdict.Should().Be(TrackerTakeVerdict.Reassigned);
        take.TookLine.Should().Contain("It replaced").And.Contain(HolderFor(provider));
        tracker.Writes.Should().ContainSingle();
        if (provider == "github")
        {
            tracker.GhCalls.Count(arguments => arguments.Contains("edit")).Should().Be(1, "one call carries the add and the removal");
            tracker.Writes[0].Should().Be(
                $"issue edit {key} --repo {Repository} --add-assignee {GitHubLogin} --remove-assignee teammate");
        }
        else
        {
            tracker.Writes[0].Should().Contain(JiraAccountId).And.NotContain("status");
        }

        IReadOnlyList<object> events = await EventsAsync(store, taskId, cts.Token);
        TrackerAssignmentReassigned reassigned = events.OfType<TrackerAssignmentReassigned>().Should().ContainSingle().Subject;
        reassigned.Reference.Should().Be(await ReferenceTextAsync(store, taskId, cts.Token));
        reassigned.ReplacedHolders.Should().ContainSingle().Which.Identity.Should().Be(HolderFor(provider));
        reassigned.AssigneeIdentity.Should().Be(provider == "jira" ? JiraAccountId : GitHubLogin);
        events.OfType<TrackerAssignmentWritten>().Should().BeEmpty("the reassignment is recorded in place of the write, not beside it");
        events.OfType<TaskAssigned>().Should().ContainSingle();

        await using IQuerySession verify = store.QuerySession();
        TaskDetails details = (await verify.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.TrackerReassignments.Should().ContainSingle().Which.ReplacedHolders
            .Select(holder => holder.Identity).Should().Equal(HolderFor(provider));
    }

    [Fact]
    public async Task A_github_reassignment_removes_every_holder_in_the_one_call_that_adds_the_operator()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "802", ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy("github", "802", "teammate", "another-teammate");
        List<TrackerClaimDecision> asked = [];

        TrackerTake? take = await ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, Door(Yes(asked)), cts.Token);

        asked.Single().Holders.Select(holder => holder.Identity).Should().Equal("teammate", "another-teammate");
        take!.Verdict.Should().Be(TrackerTakeVerdict.Reassigned, "a read-back showing the operator alone passes");
        tracker.Writes.Should().ContainSingle().Which.Should().Be(
            $"issue edit 802 --repo {Repository} --add-assignee {GitHubLogin} "
            + "--remove-assignee teammate --remove-assignee another-teammate");
        (await EventsAsync(store, taskId, cts.Token)).OfType<TrackerAssignmentReassigned>().Single()
            .ReplacedHolders.Select(holder => holder.Identity).Should().Equal("teammate", "another-teammate");
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_confirmed_offer_without_take_reassigns_and_the_door_proceeds(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-2" : "803";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        List<TrackerClaimDecision> asked = [];

        TrackerTake? take = await ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: false, offer: NotForAHeldItem, Door(Yes(asked)), cts.Token);

        asked.Should().ContainSingle().Which.Verdict.Should().Be(TrackerClaimVerdict.HeldByOther);
        take!.Verdict.Should().Be(TrackerTakeVerdict.Reassigned);
        (await EventsAsync(store, taskId, cts.Token)).OfType<TrackerAssignmentReassigned>().Should().ContainSingle();
    }

    // ── its decline ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Declining_with_take_refuses_with_nothing_written_and_the_task_unchanged(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-3" : "804";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        List<TrackerClaimDecision> asked = [];

        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, Door(No(asked)), cts.Token);

        DomainBusinessRuleException refusal = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which;
        refusal.Message.Should().Contain(HolderFor(provider)).And.Contain("nothing unattended does");
        asked.Should().ContainSingle("the human is asked once");
        tracker.Writes.Should().BeEmpty();
        await AssertUntouchedAsync(store, taskId, cts.Token);
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task Declining_without_take_warns_with_the_read_it_already_made_and_writes_nothing(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-4" : "805";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        List<TrackerClaimDecision> asked = [];

        (TrackerTake? take, TrackerClaimDecision? decision) = await ReadWithReassignAsync(
            store, taskId, tracker, take: false, offer: NotForAHeldItem, Door(No(asked)), cts.Token);

        asked.Should().ContainSingle();
        take.Should().BeNull();
        decision!.Verdict.Should().Be(TrackerClaimVerdict.HeldByOther, "the door warns with this and queues as it always did");
        decision.RefusalLine.Should().Contain("h9k task queue").And.Contain("offers to reassign");
        tracker.Writes.Should().BeEmpty();
        (await EventsAsync(store, taskId, cts.Token))
            .Where(@event => @event is TrackerAssignmentReassigned or TrackerAssignmentWritten or TrackerAssignmentObserved)
            .Should().BeEmpty();
    }

    // ── nobody to ask ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task An_unattended_take_on_a_held_item_refuses_and_is_never_offered_anything(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-5" : "806";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));

        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, reassign: null, cts.Token);

        (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message
            .Should().Contain(HolderFor(provider)).And.Contain("no flag does it, and nothing unattended does");
        tracker.Writes.Should().BeEmpty();
        await AssertUntouchedAsync(store, taskId, cts.Token);
    }

    [Fact]
    public async Task An_unattended_queue_on_a_held_item_reads_and_warns_without_asking()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "807", ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy("github", "807", "teammate");

        (TrackerTake? take, TrackerClaimDecision? decision) = await ReadWithReassignAsync(
            store, taskId, tracker, take: false, offer: NotForAHeldItem, reassign: null, cts.Token);

        take.Should().BeNull();
        decision!.Verdict.Should().Be(TrackerClaimVerdict.HeldByOther);
        tracker.Writes.Should().BeEmpty();
    }

    // ── a task another node holds ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_task_the_ledger_says_another_node_holds_is_never_offered_a_reassignment(bool withTake)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedHolderNeverReleasedAsync(store, "github", "808", ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy("github", "808", "teammate");
        TaskAggregate task = await LoadTaskAsync(store, taskId, cts.Token);
        task.State.Should().Be(TaskState.Published, "dequeued after the lease lapsed");
        task.HolderNodeId.Should().Be(OtherNode, "nothing released the holder, so the stream still names it");
        List<TrackerClaimDecision> asked = [];
        TrackerClaimCheck.ReassignDoor door = TaskAssignCommand.ReassignDoorFor(
            task, ThisNode, interactive: true, _ => null)!;
        door = door with { Offer = Yes(asked) };

        string line;
        if (withTake)
        {
            Func<Task> act = () => ReassignTakeAsync(store, ownerId, taskId, tracker, take: true, offer: null, door, cts.Token);
            line = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message;
        }
        else
        {
            (TrackerTake? take, TrackerClaimDecision? decision) = await ReadWithReassignAsync(
                store, taskId, tracker, take: false, offer: NotForAHeldItem, door, cts.Token);
            take.Should().BeNull();
            line = decision!.RefusalLine;
        }

        asked.Should().BeEmpty("the door behaves as an unattended run does");
        tracker.Writes.Should().BeEmpty();
        line.Should().Contain($"node {TaskListCommand.ShortId(OtherNode)}")
            .And.Contain($"h9k task take {TaskListCommand.ShortId(taskId)} --reason \"<why>\"");
    }

    // ── the fresh read before the write ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_holder_who_changed_after_the_offer_is_not_written_over(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-6" : "809";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        string newcomer = provider == "jira" ? "newer-account" : "newcomer";

        // The offer runs after the first read and before the write's own fresh one, so answering yes
        // while the tracker changes hands is a teammate acting while the prompt is on screen.
        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null,
            Door(_ =>
            {
                tracker.ChangeHolderTo(newcomer);
                return true;
            }),
            cts.Token);

        DomainBusinessRuleException refusal = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which;
        refusal.Message.Should().Contain(newcomer).And.Contain(HolderFor(provider)).And.Contain("nothing was written");
        tracker.Writes.Should().BeEmpty();
        await AssertUntouchedAsync(store, taskId, cts.Token);
    }

    [Fact]
    public async Task An_item_that_turned_unassigned_after_the_offer_is_not_written_either()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "810", ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy("github", "810", "teammate");

        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null,
            Door(_ =>
            {
                tracker.ChangeHolderTo(string.Empty);
                return true;
            }),
            cts.Token);

        await act.Should().ThrowAsync<DomainBusinessRuleException>();
        tracker.Writes.Should().BeEmpty();
    }

    // ── what the read-back decides ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_read_back_that_does_not_show_the_operator_is_unconfirmed_and_says_the_holders_may_be_gone(
        string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-7" : "811";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldByWith(provider, key, HolderFor(provider), writeIsInvisible: true);

        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, Door(Yes([])), cts.Token);

        string message = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message;
        message.Should().Contain("previous holders").And.Contain("may be gone from the item");
        message.Should().Contain("reading the item back afterwards did not show it holding");
        await AssertNothingRecordedAsync(store, taskId, cts.Token);
    }

    [Fact]
    public async Task A_read_back_showing_the_operator_and_somebody_else_is_contested_and_records_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedGatedAsync(store, "github", "812", ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldByWith("github", "812", "teammate", contender: "late-arrival");

        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, Door(Yes([])), cts.Token);

        string message = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message;
        message.Should().Contain("late-arrival").And.Contain("may be gone from the item");
        message.Should().NotContain("assigned to nobody", "this item was held, and the sentence for an unassigned one would be false");
        message.Should().NotContain("never removes an assignment");
        await AssertNothingRecordedAsync(store, taskId, cts.Token);
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_refused_write_quotes_the_tracker_exits_with_a_rule_refusal_and_leaves_the_holder(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-8" : "813";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldByWith(
            provider, key, HolderFor(provider), refuseWrite: "the account may not be assigned here");

        Func<Task> act = () => ReassignTakeAsync(
            store, ownerId, taskId, tracker, take: true, offer: null, Door(Yes([])), cts.Token);

        string message = (await act.Should().ThrowAsync<DomainBusinessRuleException>()).Which.Message;
        message.Should().Contain("the account may not be assigned here");
        if (provider == "github")
        {
            message.Should().Contain("current holders were left on it");
        }

        tracker.Writes.Should().BeEmpty("a refused write changes nothing on the item");
        await AssertNothingRecordedAsync(store, taskId, cts.Token);
    }

    // ── a task that is already queued ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task A_queued_task_reaches_the_offer_and_stays_in_the_queue_on_a_yes(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-9" : "814";
        Guid taskId = await SeedQueuedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        int before = (await EventsAsync(store, taskId, cts.Token)).Count;
        TaskAggregate queued = await LoadTaskAsync(store, taskId, cts.Token);
        List<TrackerClaimDecision> asked = [];

        TrackerTake? take = await OfferOnQueuedAsync(store, taskId, tracker, Door(Yes(asked)), cts.Token);

        take!.Verdict.Should().Be(TrackerTakeVerdict.Reassigned);
        asked.Should().ContainSingle();
        IReadOnlyList<object> events = await EventsAsync(store, taskId, cts.Token);
        events.Should().HaveCount(before + 1, "only the reassignment is appended");
        events[^1].Should().BeOfType<TrackerAssignmentReassigned>();
        events.OfType<TaskAssigned>().Should().ContainSingle("it is not queued a second time");
        events.OfType<TaskUnassigned>().Should().BeEmpty();
        TaskAggregate after = await LoadTaskAsync(store, taskId, cts.Token);
        after.State.Should().Be(TaskState.Queued);
        after.AssignedOwnerId.Should().Be(queued.AssignedOwnerId);
        after.PlacedOnNodeId.Should().Be(queued.PlacedOnNodeId);
    }

    [Fact]
    public async Task A_queued_task_declined_or_not_held_or_held_by_another_node_is_never_reassigned()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedQueuedAsync(store, "github", "815", ownerId, cts.Token);

        FakeTracker held = FakeTracker.HeldBy("github", "815", "teammate");
        List<TrackerClaimDecision> declined = [];
        (await OfferOnQueuedAsync(store, taskId, held, Door(No(declined)), cts.Token)).Should().BeNull();
        declined.Should().ContainSingle();

        FakeTracker unassigned = FakeTracker.Unassigned("github", "815");
        List<TrackerClaimDecision> notAsked = [];
        (await OfferOnQueuedAsync(store, taskId, unassigned, Door(Yes(notAsked)), cts.Token)).Should().BeNull();

        FakeTracker heldAgain = FakeTracker.HeldBy("github", "815", "teammate");
        (await OfferOnQueuedAsync(store, taskId, heldAgain, Door(Yes(notAsked), OtherNode), cts.Token)).Should().BeNull();

        notAsked.Should().BeEmpty("an unassigned item and another node's task are never offered");
        held.Writes.Concat(unassigned.Writes).Concat(heldAgain.Writes).Should().BeEmpty();
        await using IQuerySession verify = store.QuerySession();
        (await EventsAsync(store, taskId, cts.Token)).OfType<TrackerAssignmentReassigned>().Should().BeEmpty();
    }

    [Fact]
    public async Task Queueing_a_task_that_is_already_queued_still_refuses_as_it_always_did()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        Guid taskId = await SeedQueuedAsync(store, "github", "816", ownerId, cts.Token);
        TaskAggregate queued = await LoadTaskAsync(store, taskId, cts.Token);

        Action act = () => TaskDecider.Assign(queued, ownerId, [], Now, ownerId);

        act.Should().Throw<DomainConflictException>().WithMessage("*It is already queued; dequeue it first*");
    }

    // ── the doors that never reassign ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("jira")]
    [InlineData("github")]
    public async Task The_take_itself_still_refuses_a_held_item_so_no_sweep_and_no_force_can_reassign(string provider)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = (await NodeBootstrapSeed.NewNodeAsync(store, cts.Token)).OwnerId;
        string key = provider == "jira" ? "REAS-10" : "817";
        Guid taskId = await SeedGatedAsync(store, provider, key, ownerId, cts.Token);
        FakeTracker tracker = FakeTracker.HeldBy(provider, key, HolderFor(provider));
        await using IQuerySession session = store.QuerySession();
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(
            (await LoadTaskAsync(store, taskId, cts.Token)).ProjectId, cts.Token))!;

        TrackerTake take = await tracker.Taker().TakeAsync(
            store, project.ClaimGate, await ReferenceAsync(store, taskId, cts.Token), project.RepositoryPath, cts.Token);

        take.Verdict.Should().Be(TrackerTakeVerdict.HeldByOther);
        tracker.Writes.Should().BeEmpty();
    }

    // ── seams ─────────────────────────────────────────────────────────────────────────────────

    private async Task<TrackerTake?> ReassignTakeAsync(
        DocumentStore store,
        Guid ownerId,
        Guid taskId,
        FakeTracker tracker,
        bool take,
        TrackerClaimCheck.TakeOffer? offer,
        TrackerClaimCheck.ReassignDoor? reassign,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, token: cancellationToken))!;
        OwnerDetails owner = (await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken))!;
        await TaskAssignCommand.AppendAsync(session, task, owner, ownerId, cancellationToken);

        (TrackerTake? taken, TrackerClaimDecision? _) = await TaskAssignCommand.TakeBeforeAssigningAsync(
            store, session, task, take, tracker.Taker(), offer, reassign, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
        return taken;
    }

    private async Task<(TrackerTake? Take, TrackerClaimDecision? Decision)> ReadWithReassignAsync(
        DocumentStore store,
        Guid taskId,
        FakeTracker tracker,
        bool take,
        TrackerClaimCheck.TakeOffer? offer,
        TrackerClaimCheck.ReassignDoor? reassign,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = await LoadTaskAsync(store, taskId, cancellationToken);
        return await TaskAssignCommand.TakeBeforeAssigningAsync(
            store, session, task, take, tracker.Taker(), offer, reassign, cancellationToken);
    }

    private static async Task<TrackerTake?> OfferOnQueuedAsync(
        DocumentStore store,
        Guid taskId,
        FakeTracker tracker,
        TrackerClaimCheck.ReassignDoor door,
        CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = await LoadTaskAsync(store, taskId, cancellationToken);
        ProjectDetails project = (await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken))!;
        return await TrackerClaimCheck.OfferReassignOnQueuedAsync(
            store, taskId, project, task.ExternalReference?.ToString(), tracker.Taker(), door, cancellationToken);
    }

    private static async Task<TaskAggregate> LoadTaskAsync(
        DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();
        return (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
    }

    private static async Task<string> ReferenceTextAsync(
        DocumentStore store, Guid taskId, CancellationToken cancellationToken) =>
        (await ReferenceAsync(store, taskId, cancellationToken))!.ToString();

    private static async Task AssertUntouchedAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession verify = store.QuerySession();
        TaskListItem row = (await verify.LoadAsync<TaskListItem>(taskId, cancellationToken))!;
        row.State.Should().Be(TaskState.Published, "a refused reassignment leaves the task exactly as before");
        row.AssignedOwnerId.Should().BeNull();
        await AssertNothingRecordedAsync(store, taskId, cancellationToken);
    }

    private static async Task AssertNothingRecordedAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken) =>
        (await EventsAsync(store, taskId, cancellationToken))
            .Where(@event => @event is TrackerAssignmentReassigned or TrackerAssignmentWritten
                or TrackerAssignmentObserved or TaskAssigned)
            .Should().BeEmpty();

    private async Task<Guid> SeedQueuedAsync(
        DocumentStore store, string provider, string key, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await SeedProjectAsync(store, projectId, ClaimGate.TrackerAssignee, cancellationToken);
        if (provider == "jira")
        {
            await EnsureJiraConnectionAsync(store, ownerId, cancellationToken);
        }

        ExternalReference reference = provider == "jira"
            ? new ExternalReference(WorkItemProvider.Jira, key)
            : new ExternalReference(WorkItemProvider.GitHub, $"{Repository}#{key}");
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"queued behind {reference}", ["it is done"], TaskType.Chore, null, null, reference, Now,
            ownerId);
        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(added, ownerId, Now));
        await seed.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>
    /// A Published task whose stream still names another node as its holder: queued, claimed by that
    /// node, requeued when its lease lapsed, and dequeued, with nothing ever releasing the holder.
    /// </summary>
    private async Task<Guid> SeedHolderNeverReleasedAsync(
        DocumentStore store, string provider, string key, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid projectId = DomainId.New();
        await SeedProjectAsync(store, projectId, ClaimGate.TrackerAssignee, cancellationToken);
        ExternalReference reference = new(WorkItemProvider.GitHub, $"{Repository}#{key}");
        Guid taskId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"held elsewhere behind {reference}", ["it is done"], TaskType.Chore, null, null, reference,
            Now, ownerId);
        (TaskAggregate task, object[] events) = TaskSeed.Start(added, ownerId, Now);

        TaskClaimed claimed = new(taskId, OtherNode, ownerId, 1, DomainId.New(), Now, OwnerRootFingerprint: "other-root");
        task.Apply(claimed);
        TaskRequeued requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
        task.Apply(requeued);
        TaskUnassigned dequeued = TaskDecider.Dequeue(task, null, leaseHeld: false, Now, ownerId);

        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(taskId, [.. events, claimed, requeued, dequeued]);
        await seed.SaveChangesAsync(cancellationToken);
        return taskId;
    }
}
