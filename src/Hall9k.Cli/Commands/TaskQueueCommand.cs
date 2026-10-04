using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The go signal (Decisions Log #34, and the assign/queue split): Published -> Queued, or Blocked
/// until every dependency has closed out. It queues the task for its assignee and only for them, so
/// it is the assignee's act: with no assignee the actor becomes the assignee through the very
/// <see cref="TaskAssigned"/> this appends, and with another member as assignee it refuses and names
/// that member's hand-off, <c>h9k task assign &lt;id&gt; &lt;member&gt;</c>. Always a human's explicit
/// act; the platform never queues a task on its own judgment.
/// </summary>
public sealed class TaskQueueCommand : Hall9kAsyncCommand<TaskQueueCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--take")]
        [Description(
            "In a project whose claim gate is tracker-assignee, take the linked Jira card or GitHub "
            + "issue for this install's own tracker identity as part of queueing, so one command moves "
            + "the tracker and the board together and the gate then passes on its own. Reads the item "
            + "fresh first and writes ONLY when it shows no assignee: an item somebody else holds is "
            + "refused (exit 70) and nothing is written, and there is no flag that takes one from "
            + "another person. Already yours records the observation and proceeds. The write is a FIELD "
            + "UPDATE and never a transition, but a team's own automation (a Jira board rule, a GitHub "
            + "workflow) may react to an assignment. Without this flag an interactive run offers the "
            + "same take on an unassigned item, and a non-interactive one warns and proceeds without "
            + "writing anything")]
        public bool Take { get; init; }

        [CommandOption("--node [NODE]")]
        [Description(
            "Places this task on one of your own nodes (its id, or an unambiguous fragment): only that "
            + "node's own dispatcher claims it, and every other node of yours stands down without a "
            + "forced take. Your fleet is your root node, the one whose key established it in this "
            + "project's ledger, plus every node currently vouched into it (a root never needs h9k node "
            + "vouch against itself). Refused for a node outside both sets (h9k node vouch <id> first). "
            + "The bare flag, with nothing named, clears an existing placement so any of your nodes may "
            + "claim it again; omit the option entirely to leave whatever placement the task already "
            + "carries untouched. h9k task show and h9k status name the placed node")]
        public FlagValue<string> Node { get; init; } = new();
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        OwnerDetails actor = await session.LoadAsync<OwnerDetails>(context.OwnerId, cancellationToken)
            ?? throw new DomainNotFoundException($"This node's owner {context.OwnerId} is not registered.");

        // A task that is not Published never reaches the owner checks: TaskDecider.Assign refuses it
        // below with the reason and the command that fixes it, whoever's it is.
        if (task.State == TaskState.Published)
        {
            await AuthorizeAsync(session, task, context, actor, cancellationToken);
        }
        else if (task.State.IsAssigned && settings.Node.IsSet)
        {
            string queuedId = TaskListCommand.ShortId(task.Id);
            throw new DomainConflictException(
                $"Task {queuedId} is already queued, so --node here would be a second queueing. To change where it "
                + $"runs: h9k task assign {queuedId} --node [NODE]. To take it out of the queue: h9k task dequeue {queuedId}.");
        }

        Optional<Guid?> placement = await TaskAssignCommand.ResolvePlacementAsync(
            session, settings.Node, actor.RootFingerprint, ownerIsThisInstall: true, task.ProjectId, context.NodeId,
            new GitLedgerChainReader(), cancellationToken);
        TaskAssigned queued = await TaskAssignCommand.AppendAsync(
            session, task, actor, context.OwnerId, cancellationToken, placement);

        // Composed above, committed below, and the tracker written to in between: the order is the whole
        // of "one command moves the tracker and the board together". The take is the only part of this
        // command that can refuse the queueing, and it throws before the save, so a take that could not
        // have the item leaves the task exactly as it was. Everything else about the gate is read after
        // the commit, because the tracker is the go signal and a task waiting in the queue is the design
        // (Decisions Log #142, #143).
        (TrackerTake? take, TrackerClaimDecision? decision) = await TaskAssignCommand.TakeBeforeAssigningAsync(
            store, session, task, settings.Take, trackerAssignmentTake: null, TaskAssignCommand.Offer(), cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-assigned:{taskId}", cancellationToken);

        await TaskAssignCommand.AnnounceAsync(
            queued, actor, session, cancellationToken, task.StackedOnTaskId, StackedParentDeclaration.From(task));
        await TaskAssignCommand.ReportTrackerAsync(store, session, task, take, decision, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Whether this node's owner may queue <paramref name="task"/>: a member takes a Published task nobody
    /// holds for itself, as <c>h9k task start</c> does and the receive gate allows, and otherwise the
    /// task's own assignee queues it. Another member as assignee refuses and names the hand-off. A task
    /// whose ledger holder lock names someone else is not nobody's, so it goes through the owner rule too.
    /// </summary>
    internal static async Task AuthorizeAsync(
        IDocumentSession session, TaskAggregate task, BootstrapContext context, OwnerDetails actor,
        CancellationToken cancellationToken)
    {
        await TaskOwnerGuard.AssertAssigneeMayQueueAsync(session, task, context, actor.RootFingerprint, cancellationToken);
        if (task.AssigneeOwnerId is not null || task.HolderOwnerRootFingerprint is not null)
        {
            await TaskOwnerGuard.AssertMayActAsync(session, task, context, cancellationToken);
        }
    }
}
