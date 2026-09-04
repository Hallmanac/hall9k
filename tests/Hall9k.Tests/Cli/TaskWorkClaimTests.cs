using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The atomic decision behind h9k task work's Published entry (task 688a1ccf-h9k):
/// <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/> composes
/// <see cref="TaskDecider.Assign"/> and <see cref="TaskDecider.ClaimInteractively"/> into one
/// unit, with no session and no append, so the composition itself is pinned here without a
/// database — the concurrency arbitration this composition feeds is pinned separately, against a
/// real store, in TaskWorkClaimConcurrencyTests. <see cref="TaskWorkCommand.PrepareInteractiveClaimFromBlocked"/>'s
/// own sibling composition, for the already-Blocked entry (task 0ac72cb8-h9k), is pinned in the
/// second half of this file — it mirrors TaskStartClaimTests's identical shape, carry-forward case
/// included: h9k task start's own Blocked entry gained the identical carry-forward behavior in the
/// same task, closing the gap task 8a56af78-h9k had originally left open.
/// </summary>
// Capture (below) swaps the process-wide AnsiConsole.Console, the same static
// InstallCommandTests and others in this collection swap; sharing the collection serializes
// this class against them too (review, PR #192).
[Collection("Hall9kHome")]
public sealed class TaskWorkClaimTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void A_published_task_with_no_open_dependencies_is_assigned_and_claimed_in_one_unit()
    {
        TaskAggregate task = PublishedTask();
        Guid runId = DomainId.New();

        (TaskAssigned assigned, TaskClaimed claimed, IReadOnlyList<TaskDependency> unmet) =
            TaskWorkCommand.PrepareInteractiveClaimFromPublished(
                task, Owner, [], runId, Now, acknowledgeUnmetDependencies: false);

        assigned.AssignedOwnerId.Should().Be(Owner);
        assigned.UnmetDependencies.Should().BeEmpty();
        unmet.Should().BeEmpty();

        claimed.NodeId.Should().Be(Guid.Empty, "an interactive claim carries the sentinel node id");
        claimed.OwnerId.Should().Be(Owner);
        claimed.RunId.Should().Be(runId);
        claimed.LeaseGeneration.Should().Be(1);
        claimed.DependencyOverrideAcknowledged.Should().BeFalse("nothing needed overriding");

        // Mutated in place (mirrors TaskPublishCommand's own append-then-Apply composition), so
        // the caller's own aggregate reflects exactly what the atomic append is about to commit —
        // Queued, never Claimed, since this helper hands the claim event back rather than
        // applying it itself.
        task.State.Should().Be(TaskState.Queued);
        task.AssignedOwnerId.Should().Be(Owner);
    }

    [Fact]
    public void A_published_task_with_a_closed_out_dependency_is_still_assigned_and_claimed()
    {
        TaskDependency closed = ClosedDependency();
        TaskAggregate task = PublishedTask(closed.Id);

        (TaskAssigned assigned, TaskClaimed claimed, IReadOnlyList<TaskDependency> unmet) =
            TaskWorkCommand.PrepareInteractiveClaimFromPublished(
                task, Owner, [closed], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        assigned.UnmetDependencies.Should().BeEmpty("the one dependency has already closed out");
        unmet.Should().BeEmpty();
        claimed.OwnerId.Should().Be(Owner);
        task.State.Should().Be(TaskState.Queued);
    }

    [Fact]
    public void A_published_task_with_an_open_dependency_and_no_acknowledgment_is_refused_and_names_the_blocker()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = PublishedTask(open.Id);

        Action act = () => TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            task, Owner, [open], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s) that have not closed out*")
            .Where(exception => exception.Message.Contains(open.Describe())
                    && exception.Message.Contains("--acknowledge-unmet-dependencies"),
                "the refusal names the open blocker and the exact override flag to re-run with");

        // The refusal is up front: nothing about the task was decided, so it stays exactly what
        // it was handed in as, and a caller that retries once the blocker clears (or with the
        // flag) reads a task still Published rather than one half-assigned toward a Blocked
        // landing it never wanted.
        task.State.Should().Be(TaskState.Published);
        task.AssignedOwnerId.Should().BeNull();
    }

    /// <summary>
    /// The conversion this task (0ac72cb8-h9k) makes: an open dependency no longer refuses
    /// outright — with the acknowledgment, the assignment and claim proceed anyway, landing
    /// Blocked (not Claimed straight through, since the claim is what h9k task work commits) with
    /// the override recorded, the same "the platform advises rather than refuses" shape h9k task
    /// start already has.
    /// </summary>
    [Fact]
    public void A_published_task_with_an_open_dependency_and_acknowledgment_is_assigned_and_claimed_anyway()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = PublishedTask(open.Id);
        Guid runId = DomainId.New();

        (TaskAssigned assigned, TaskClaimed claimed, IReadOnlyList<TaskDependency> unmet) =
            TaskWorkCommand.PrepareInteractiveClaimFromPublished(
                task, Owner, [open], runId, Now, acknowledgeUnmetDependencies: true);

        assigned.UnmetDependencies.Should().ContainSingle().Which.Should().Be(open.Id);
        unmet.Should().ContainSingle().Which.Should().Be(open);
        claimed.NodeId.Should().Be(Guid.Empty);
        claimed.DependencyOverrideAcknowledged.Should().BeTrue("the human overrode the open dependency deliberately");
        claimed.DependencyOverrideCarriedForward.Should().BeFalse("a fresh assignment has nothing to carry forward from");
        claimed.RunId.Should().Be(runId);

        task.State.Should().Be(TaskState.Blocked);
        task.AssignedOwnerId.Should().Be(Owner);
    }

    /// <summary>
    /// A blocker that is dead (<see cref="TaskDependency.IsDead"/>) will never close out, so the
    /// refusal must not promise it "queues itself the moment the last one's pull request merges"
    /// — <see cref="TaskDependency.DescribeDeath"/>'s honest remedy is what belongs here instead
    /// (independent pre-PR review, cycle 1: conformance finding at TaskWorkCommand.cs:546,
    /// adversarial finding at TaskWorkCommand.cs:545 — the same defect from both lenses).
    /// </summary>
    [Fact]
    public void A_published_task_with_a_dead_dependency_is_refused_without_a_false_merge_promise()
    {
        TaskDependency dead = DeadDependency();
        TaskAggregate task = PublishedTask(dead.Id);

        Action act = () => TaskWorkCommand.PrepareInteractiveClaimFromPublished(
            task, Owner, [dead], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s) that have not closed out*")
            .Where(exception => exception.Message.Contains(dead.DescribeDeath())
                    && !exception.Message.Contains("queues itself the moment"),
                "a dead blocker's pull request will never merge, so the ordinary queues-itself "
                + "promise must not be made for it");
    }

    [Fact]
    public void A_blocked_task_with_no_acknowledgment_is_refused_and_names_the_blocker()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = BlockedTask(open);

        Action act = () => TaskWorkCommand.PrepareInteractiveClaimFromBlocked(
            task, Owner, [open], DomainId.New(), Now, acknowledgeUnmetDependencies: false);

        act.Should().Throw<DomainBusinessRuleException>()
            .WithMessage("*Blocked*")
            .Where(exception => exception.Message.Contains(open.Describe())
                    && exception.Message.Contains("--acknowledge-unmet-dependencies")
                    // An already-assigned task cannot be pointed at h9k task assign, which
                    // refuses anything but a Published task.
                    && !exception.Message.Contains("h9k task assign"),
                "the refusal names the open blocker and the override flag, without advice the task cannot follow");

        task.State.Should().Be(TaskState.Blocked, "the refusal decides nothing");
    }

    [Fact]
    public void A_blocked_task_with_a_fresh_acknowledgment_claims_and_records_it_as_not_carried_forward()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = BlockedTask(open);
        Guid runId = DomainId.New();

        (TaskClaimed claimed, bool carriedForward) = TaskWorkCommand.PrepareInteractiveClaimFromBlocked(
            task, Owner, [open], runId, Now, acknowledgeUnmetDependencies: true);

        carriedForward.Should().BeFalse("the flag was passed fresh this time, not carried from an earlier claim");
        claimed.NodeId.Should().Be(Guid.Empty);
        claimed.RunId.Should().Be(runId);
        claimed.DependencyOverrideAcknowledged.Should().BeTrue();
        claimed.DependencyOverrideCarriedForward.Should().BeFalse();
    }

    /// <summary>
    /// The carry-forward this task (0ac72cb8-h9k) adds on top of h9k task start's own shape
    /// (design ruling R7): once an earlier claim on this same task acknowledged this exact
    /// blocker (recorded on <see cref="TaskAggregate.AcknowledgedUnmetDependencyIds"/> — the
    /// durable record a handback or a retry does not clear, unlike <see cref="TaskAggregate.UnmetDependencies"/>'s
    /// own claim-scoped sibling), a later reclaim of the identical still-open blocker does not
    /// need the flag again and is recorded as relying on that earlier acknowledgment.
    /// </summary>
    [Fact]
    public void A_blocked_task_already_acknowledged_by_an_earlier_claim_does_not_need_the_flag_again()
    {
        TaskDependency open = OpenDependency();
        TaskAggregate task = BlockedTask(open);
        task.Apply(TaskDecider.ClaimInteractively(
            task, Owner, DomainId.New(), Now, dependencyOverrideAcknowledged: true));
        task.Apply(TaskDecider.HandBack(task, task.CurrentRunId!.Value, "task/x-y", "handing back", Now, Owner));
        task.State.Should().Be(TaskState.Blocked, "the same still-open blocker is on record unmet");
        task.UnmetDependenciesAlreadyAcknowledged.Should().BeTrue();

        Guid runId = DomainId.New();
        (TaskClaimed claimed, bool carriedForward) = TaskWorkCommand.PrepareInteractiveClaimFromBlocked(
            task, Owner, [open], runId, Now, acknowledgeUnmetDependencies: false);

        carriedForward.Should().BeTrue("this exact blocker was already acknowledged by the earlier claim");
        claimed.DependencyOverrideAcknowledged.Should().BeTrue();
        claimed.DependencyOverrideCarriedForward.Should().BeTrue();
    }

    /// <summary>
    /// A claim that proceeds — fresh or carried forward — deserves the same honesty the refusal
    /// path already gives a dead blocker (independent pre-PR review, cycle 1, adversarial lens): a
    /// carried-forward acknowledgment given against a once-live blocker that has since died would
    /// otherwise proceed printing only the blocker's bare state, with
    /// <see cref="TaskDependency.DescribeDeath"/>'s "will never close out on its own" advice never
    /// shown anywhere.
    /// </summary>
    [Fact]
    public void PrintUnmetDependencyWarning_names_a_dead_blocker_even_when_the_acknowledgment_is_carried_forward()
    {
        TaskDependency dead = DeadDependency();

        string output = Capture(() => TaskWorkCommand.PrintUnmetDependencyWarning(
            "Claiming", DomainId.New(), [dead], carriedForward: true));

        output.Should().Contain(dead.Describe());
        output.Should().Contain(dead.DescribeDeath());
    }

    /// <summary>The global console, swapped for a writer and put back — mirrors InstallCommandTests's own capture.</summary>
    private static string Capture(Action action)
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
            action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private static TaskAggregate BlockedTask(TaskDependency open)
    {
        TaskAggregate task = PublishedTask(open.Id);
        task.Apply(TaskDecider.Assign(task, Owner, [open], Now, Owner));
        task.State.Should().Be(TaskState.Blocked);
        return task;
    }

    private static TaskDependency DeadDependency() => new(
        DomainId.New(), "A blocker that was abandoned", TaskState.Abandoned, IsClosedOut: false,
        CurrentRunState: null, PullRequestUrl: null, TaskType.Chore, []);

    private static TaskAggregate PublishedTask(params Guid[] blockedBy)
    {
        TaskAggregate task = new();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Prove the atomic claim composes assign and claim",
            ["one event append, exactly one winner"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null,
            addedAt: Now, addedByOwnerId: Owner, blockedBy: blockedBy);
        task.Apply(added);

        // Publish's own graph only needs to know each blocker exists, to clear its cycle check —
        // the dependency snapshot that actually drives Assign's decision in each test below is
        // the caller-supplied list passed straight to PrepareInteractiveClaimFromPublished.
        TaskDependencyGraph graph = blockedBy.Length == 0
            ? TaskDependencyGraph.Empty
            : new TaskDependencyGraph(blockedBy.Select(id => new TaskDependency(
                id, "A blocker", TaskState.Done, IsClosedOut: true, CurrentRunState: null,
                PullRequestUrl: null, TaskType.Chore, [])));
        task.Apply(TaskDecider.Publish(task, graph, Now, Owner));
        return task;
    }

    private static TaskDependency ClosedDependency() => new(
        DomainId.New(), "A blocker already merged", TaskState.Done, IsClosedOut: true,
        CurrentRunState: RunState.Completed, PullRequestUrl: "https://github.com/x/y/pull/1",
        TaskType.Chore, []);

    private static TaskDependency OpenDependency() => new(
        DomainId.New(), "A blocker still running", TaskState.Claimed, IsClosedOut: false,
        CurrentRunState: RunState.Running, PullRequestUrl: null,
        TaskType.Chore, []);
}
