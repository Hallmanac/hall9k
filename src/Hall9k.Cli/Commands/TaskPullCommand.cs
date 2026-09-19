using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Asks this project's other members for one task's whole event stream, by id, without needing a
/// linked tracker item to hang the ask on (task a56cf16e). The deliberate, human-typed twin of the
/// broadcast <c>h9k task add --from-issue</c> already queues when the ledger names a task this node
/// does not hold: same <c>EventCatchUpCoordinator.RequestStreamBroadcastAsync</c>, same envelope,
/// same answering path — and the answering node serves it from below its own replication switch-on
/// point, because an explicit ask is the opt-in that lifts that exclusion
/// (<c>EventCatchUpResponder</c>).
/// <para>
/// Touches no git and no network of its own: it queues an envelope in this node's store and the
/// daemon's next message sweep is what actually sends it, exactly like <c>h9k message send</c>.
/// </para>
/// <para>
/// An absent stream is the one shape this can fill. A stream this node holds only the TAIL of —
/// the post-switch-on half an ordinary flush shipped, with no <c>TaskAdded</c> on it — is refused
/// up front instead (<c>EventStreamCatchUp.PartiallyHeldRefusal</c>): an answer's older events
/// would land behind the newer ones already here, which <c>EventReplicationInbox</c> refuses on
/// arrival rather than replay a stream backwards.
/// </para>
/// </summary>
public sealed class TaskPullCommand : Hall9kAsyncCommand<TaskPullCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK-ID>")]
        [Description(
            "The task's full id. A short fragment only works for a task this node already holds — "
            + "the whole point of this command is a task it does not, and there is nothing local to "
            + "match a fragment against. Read the full id off the node that has it (h9k task show "
            + "prints it) or off its own ledger record filename, records/<task-id>.yaml.")]
        public string TaskId { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project whose members are asked: its name, an unambiguous fragment of it, or its "
            + "full id (h9k project list shows them all). Defaults to this node's only eligible "
            + "project (not archived, with a repository) when there is exactly one; with more than "
            + "one registered, this is required.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    /// <summary>The whole command body, with its session handed in rather than opened through
    /// <see cref="CliStore.Open()"/> — this codebase's CLI commands have no other test seam, the
    /// same shape <c>MessageSendCommand.RunAsync</c> already takes.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        Guid taskId = await ResolveTaskIdAsync(session, settings.TaskId, cancellationToken);

        // Answered before the project is resolved at all, because it does not depend on one: a task
        // already here needs no ask, and making the human disambiguate --project first to be told
        // that would be a refusal standing in front of an answer.
        EventStreamCatchUp.LocalStreamHold hold =
            await EventStreamCatchUp.ClassifyLocalHoldAsync(session, taskId, cancellationToken);
        string heldShortId = TaskListCommand.ShortId(taskId);
        switch (hold)
        {
            // Every id in this platform is a stream id, so a project, idea, epic, run or node id
            // pasted here finds a stream and would otherwise be reported back as a task already
            // held, with an h9k task show that then fails (independent pre-PR review, cycle 1,
            // adversarial lens, low).
            case EventStreamCatchUp.LocalStreamHold.NotATask:
                throw new DomainValidationException(
                    $"'{settings.TaskId}' names a stream this node holds, but not a task's — it is some other "
                    + "kind of id (a project, idea, epic, run, or node). h9k task pull only pulls a task's own "
                    + "event stream: read the task's full id off the node that holds it (h9k task show prints "
                    + "it) or off its own ledger record filename, records/<task-id>.yaml.");

            // A tail-only stream is the shape this command was most likely to be reached for and
            // the one it can do least about: reporting it as already held sent the human to an
            // h9k task show with no objective on it, and queueing the ask anyway would only earn a
            // record the receiving inbox refuses (independent pre-PR review, cycle 4, adversarial
            // lens, medium).
            case EventStreamCatchUp.LocalStreamHold.Partial:
                throw new DomainValidationException(
                    EventStreamCatchUp.PartiallyHeldRefusal(
                        heldShortId,
                        $"h9k task show {heldShortId} shows the part that did arrive; the whole history is still "
                        + "on the node that produced it."));

            case EventStreamCatchUp.LocalStreamHold.Whole:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]This node already holds task[/] [bold]{heldShortId}[/][dim]'s event stream — nothing to pull.[/]");
                AnsiConsole.MarkupLineInterpolated($"[dim]See it with h9k task show {heldShortId}.[/]");
                return ExitCodes.Ok;

            default:
                break;
        }

        ProjectDetails project = await EligibleProject.ResolveAsync(
            session, settings.Project, "this task should be pulled into", cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);

        EventStreamCatchUp.RequestDisposition disposition = await EventStreamCatchUp.RequestStreamAsync(
            session, project.Id, taskId, context.NodeId, ownerRootFingerprint, DateTimeOffset.UtcNow, cancellationToken);
        if (disposition == EventStreamCatchUp.RequestDisposition.NoOwnerRoot)
        {
            throw new DomainValidationException(
                EventStreamCatchUp.TaskPullBlockedRefusal(taskId.ToString(), project.Name));
        }

        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        string standing = (disposition, project.IsEligibleForMessaging()) switch
        {
            // An explicit --project skips EligibleProject.ResolveAsync's own eligibility check
            // (that method's own doc), so this ask may have queued for a project the sweep can
            // never flush today (archived, or with no repository yet) — "the daemon's next message
            // sweep sends it" is an outright false promise there, not merely an optimistic one. The
            // same correction MessageSendCommand and TaskHandoffCommand already carry, in their own
            // words (independent pre-PR review, cycle 1, adversarial lens, low).
            (EventStreamCatchUp.RequestDisposition.Queued, true) => "the daemon's next message sweep sends it",
            (EventStreamCatchUp.RequestDisposition.Queued, false) =>
                "it stays queued until this project is eligible for messaging (not archived, with a repository) — "
                + "the daemon's sweep cannot send it yet",
            (_, true) => "an identical request from an earlier run is still outstanding, so nothing new was queued",
            (_, false) =>
                "an identical request from an earlier run is already queued, and stays queued until this project "
                + "is eligible for messaging (not archived, with a repository)",
        };
        AnsiConsole.MarkupLineInterpolated(
            $"[blue]Asked[/] {project.Name}'s other members for task [bold]{shortId}[/]'s event stream [dim]({standing})[/].");
        AnsiConsole.MarkupLine(
            "[dim]A broadcast never times out: it stays outstanding until some member's answer applies here. "
            + "h9k status shows it while it stands, and the task appears on this node's board once it lands.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// A full id straight through — the only form that can name a stream this node does not hold —
    /// falling back to <see cref="TaskIdResolver"/>'s own fragment match against local tasks, which
    /// can only ever resolve to one this node DOES hold and so lands on the already-here report
    /// above. A fragment that matches nothing locally gets this command's own refusal rather than
    /// <see cref="TaskIdResolver"/>'s "no task matches", which reads like the id is wrong when the
    /// real answer is that a fragment cannot name an absent stream.
    /// </summary>
    private static async Task<Guid> ResolveTaskIdAsync(
        IQuerySession session, string taskIdOrFragment, CancellationToken cancellationToken)
    {
        if (taskIdOrFragment.IsBlank())
        {
            throw new DomainValidationException("h9k task pull needs a task id — pass the full id of the task to pull.");
        }

        if (Guid.TryParse(taskIdOrFragment, out Guid full))
        {
            return full;
        }

        try
        {
            return await TaskIdResolver.ResolveAsync(session, taskIdOrFragment, cancellationToken);
        }
        catch (DomainNotFoundException)
        {
            throw new DomainValidationException(
                $"'{taskIdOrFragment}' matches no task on this node, and a fragment can only ever match one "
                + "that is already here. Pass the task's full id instead — h9k task show prints it on the node "
                + "that holds the task, and its ledger record is records/<task-id>.yaml.");
        }
    }
}
