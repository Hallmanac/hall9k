using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
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
/// (<c>EventCatchUpResponder</c>), along with every run stream it holds for that task, so a pulled
/// task whose runs finished reads Done rather than Delivered forever (task 9eb5b245).
/// <para>
/// Queues an envelope in this node's store and stops: the daemon's next message sweep is what
/// actually sends it, exactly like <c>h9k message send</c>. It touches git in one case only, and
/// never a remote of its own beyond that ref fetch — when it has to read this project's ledger
/// records, which is the only local source that can turn a SHORT id into a full one, or say which
/// project holds a task whose own stream is not here (<see cref="LedgerTaskLookup"/>). A full id on
/// a node with one eligible project, or with <c>--project</c> named, never reaches that read.
/// </para>
/// <para>
/// An absent stream is the one shape this can fill. A stream this node holds only the TAIL of —
/// the post-switch-on half an ordinary flush shipped, with no <c>TaskAdded</c> on it — is refused
/// up front instead (<c>EventStreamCatchUp.PartiallyHeldRefusal</c>): an answer's older events
/// would land behind the newer ones already here, which <c>EventReplicationInbox</c> refuses on
/// arrival rather than replay a stream backwards. The refusal names the way out rather than calling
/// it hopeless: the daemon's own startup repair
/// (<c>Hall9k.Domain.Infrastructure.Persistence.HeadlessReplicatedStreamRepair</c>) holds that tail
/// and frees the stream id, after which this same command reads the stream as absent and asks. It
/// names the way out conditionally, because a stream that repair cannot reconstruct faithfully is
/// left alone and reported only in the daemon's log: see
/// <c>EventStreamCatchUp.PartiallyHeldRefusal</c>.
/// </para>
/// <para>
/// A task already here is not a dead end either: its own blocked-by and stacked-on dependencies are
/// checked, and any whose stream is absent is asked for, which is what keeps
/// <c>h9k task assign</c> from refusing a task for a dependency the platform could have fetched
/// (<see cref="TaskDependencyCatchUp"/> — the same asks the platform mints on its own the moment a
/// task lands by replication).
/// </para>
/// </summary>
public sealed class TaskPullCommand : Hall9kAsyncCommand<TaskPullCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK-ID>")]
        [Description(
            "The task's full id, its short id, or an unambiguous fragment of either. A fragment "
            + "resolves against this node's own tasks first, and then against this project's ledger "
            + "records (records/<task-id>.yaml), which every member can read and which is how the "
            + "short id off a board row or a branch name names a task whose stream is not here. A "
            + "run's own stream id works too, for a run this node is missing on a task it already "
            + "holds.")]
        public string TaskId { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "The project whose members are asked: its name, an unambiguous fragment of it, or its "
            + "full id (h9k project list shows them all). Defaults to the one project whose ledger "
            + "names this task, or, failing that, to this node's only eligible project (not "
            + "archived, with a repository); with several and no ledger record naming it, this is "
            + "required.")]
        public string? Project { get; init; }

        [CommandOption("--again")]
        [Description(
            "Close out the request still outstanding from an earlier run as superseded and ask "
            + "afresh. Only needed when this command reports one outstanding: a request that has "
            + "already closed — declined by a peer, or answered — is re-asked without this flag, "
            + "because a closed request is not an ask in flight. This is also the only way to clear "
            + "a request minted before v0.10.5, which the inbox of the day left standing on a "
            + "decline it only ever applied to a candidate cascade.")]
        public bool Again { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(
            session, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), settings, cancellationToken);
    }

    /// <summary>The whole command body, with its session and its ledger handed in rather than
    /// opened through <see cref="CliStore.Open()"/> — this codebase's CLI commands have no other
    /// test seam, the same shape <c>MessageSendCommand.RunAsync</c> already takes.</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, ILedger ledger, Settings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> eligible = [.. (await session.Query<ProjectDetails>().ToListAsync(cancellationToken))
            .Where(candidate => candidate.IsEligibleForMessaging())
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)];

        Resolution resolution = await ResolveAsync(session, ledger, eligible, settings.TaskId, cancellationToken);
        Guid streamId = resolution.StreamId;

        // Answered before the project is resolved at all, because it does not depend on one: a task
        // already here needs no ask, and making the human disambiguate --project first to be told
        // that would be a refusal standing in front of an answer.
        EventStreamCatchUp.LocalStreamHold hold =
            await EventStreamCatchUp.ClassifyLocalHoldAsync(session, streamId, cancellationToken);
        string heldShortId = TaskListCommand.ShortId(streamId);
        switch (hold)
        {
            // Every id in this platform is a stream id, so a project, idea, epic or node id pasted
            // here finds a stream and would otherwise be reported back as a task already held, with
            // an h9k task show that then fails (independent pre-PR review, cycle 1, adversarial
            // lens, low).
            case EventStreamCatchUp.LocalStreamHold.NotPullable:
                throw new DomainValidationException(
                    $"'{settings.TaskId}' names a stream this node holds, but neither a task's nor a run's — it "
                    + "is some other kind of id (a project, idea, epic, or node). h9k task pull only pulls a "
                    + "task's own event stream, or one of its runs': read the task's full id off the node that "
                    + "holds it (h9k task show prints it) or off its own ledger record filename, "
                    + "records/<task-id>.yaml.");

            // A tail-only stream is the shape this command was most likely to be reached for and
            // the one it can do least about: reporting it as already held sent the human to an
            // h9k task show with no objective on it, and queueing the ask anyway would only earn a
            // record the receiving inbox refuses (independent pre-PR review, cycle 4, adversarial
            // lens, medium).
            case EventStreamCatchUp.LocalStreamHold.Partial:
                throw new DomainValidationException(
                    EventStreamCatchUp.PartiallyHeldRefusal(
                        $"Task {heldShortId}",
                        $"h9k task show {heldShortId} shows the part that did arrive; re-run this command after "
                        + "the daemon's next start, and if it refuses this way again, read the daemon's log for "
                        + "this stream before re-running a third time."));

            // The same shape and the same way out: the startup repair reads a run's genesis exactly
            // as it reads a task's (PartialReplicatedStreamRules), so a partly-held run ends the
            // same wait at the daemon's next start rather than being the one kind this cannot free.
            case EventStreamCatchUp.LocalStreamHold.RunPartial:
                throw new DomainValidationException(
                    EventStreamCatchUp.PartiallyHeldRefusal(
                        $"Run {heldShortId}",
                        "The whole run is still on the node that produced it; re-run this command after the "
                        + "daemon's next start, and if it refuses this way again, read the daemon's log for "
                        + "this stream before re-running a third time."));

            case EventStreamCatchUp.LocalStreamHold.RunWhole:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]This node already holds run[/] [bold]{heldShortId}[/][dim]'s event stream — nothing to pull.[/]");
                return ExitCodes.Ok;

            case EventStreamCatchUp.LocalStreamHold.Whole:
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]This node already holds task[/] [bold]{heldShortId}[/][dim]'s event stream — nothing to pull.[/]");
                await AskForMissingDependenciesAsync(session, streamId, cancellationToken);
                AnsiConsole.MarkupLineInterpolated($"[dim]See it with h9k task show {heldShortId}.[/]");
                return ExitCodes.Ok;

            default:
                break;
        }

        ProjectDetails project = await ResolveProjectAsync(
            session, ledger, eligible, settings.Project, resolution, streamId, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);

        EventStreamCatchUp.RequestDisposition disposition = await EventStreamCatchUp.RequestStreamAsync(
            session, project.Id, streamId, context.NodeId, ownerRootFingerprint, DateTimeOffset.UtcNow,
            cancellationToken, settings.Again);
        if (disposition == EventStreamCatchUp.RequestDisposition.NoOwnerRoot)
        {
            throw new DomainValidationException(
                EventStreamCatchUp.TaskPullBlockedRefusal(streamId.ToString(), project.Name));
        }

        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(streamId);
        string standing = Standing(disposition, shortId, project.IsEligibleForMessaging());
        AnsiConsole.MarkupLineInterpolated(
            $"[blue]Asked[/] {project.Name}'s other members for task [bold]{shortId}[/]'s event stream [dim]({standing})[/].");
        // "Never times out" is still true and "never closes" never was, since v0.10.5: a broadcast
        // has no candidate cascade to exhaust and no per-candidate clock behind it, but a member
        // answering events-unavailable closes it on the spot, because that is the only answer a
        // member holding nothing will ever send and the ask would otherwise stand forever. Saying
        // only the first half sent a human back to re-run this command against a request that had
        // already been refused hours earlier (2026-09-19 23:36).
        AnsiConsole.MarkupLine(
            "[dim]A member that holds the stream answers with the task's own events and its runs'; one that "
            + "holds nothing declines, which closes the request. h9k status shows it while it stands and, once "
            + "it is closed, which node declined it and when. The task appears on this node's board once an "
            + "answer lands, and any dependency it names whose stream is not here is asked for the same way, "
            + "on its own.[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What this run's own ask came to, in one clause. An explicit <c>--project</c> skips
    /// <see cref="EligibleProject.ResolveAsync"/>'s own eligibility check (that method's own doc),
    /// so an ask may have queued for a project the sweep can never flush today (archived, or with
    /// no repository yet) — "the daemon's next message sweep sends it" is an outright false promise
    /// there, not merely an optimistic one. The same correction <c>MessageSendCommand</c> and
    /// <c>TaskHandoffCommand</c> already carry, in their own words (independent pre-PR review,
    /// cycle 1, adversarial lens, low).
    /// </summary>
    private static string Standing(
        EventStreamCatchUp.RequestDisposition disposition, string shortId, bool projectEligibleForMessaging)
    {
        string carriage = projectEligibleForMessaging
            ? "the daemon's next message sweep sends it"
            : "it stays queued until this project is eligible for messaging (not archived, with a repository) — "
                + "the daemon's sweep cannot send it yet";
        return disposition switch
        {
            EventStreamCatchUp.RequestDisposition.Queued => carriage,
            EventStreamCatchUp.RequestDisposition.ReAsked =>
                $"every earlier request for it had already closed, so this run asked again; {carriage}",
            EventStreamCatchUp.RequestDisposition.Superseded =>
                $"the request outstanding from an earlier run was closed out as superseded; {carriage}",
            _ => "an identical request from an earlier run is still outstanding, so nothing new was queued — "
                + $"h9k task pull {shortId} --again closes it out and asks afresh",
        };
    }

    /// <summary>
    /// Asks for every blocked-by or stacked-on dependency of a task this node already holds whose
    /// own stream is not here — the platform mints these on its own the moment a task lands by
    /// replication (<see cref="TaskDependencyCatchUp"/>), so this is the lever for a task that
    /// landed before it did, and the answer to the one refusal a human cannot otherwise act on:
    /// <c>h9k task assign</c> turning the task down because the platform does not know a task it
    /// depends on. The project asked is the task's own, read off its projection rather than
    /// resolved or defaulted — a task already here has no ambiguity about which project it is in.
    /// </summary>
    private static async Task AskForMissingDependenciesAsync(
        IDocumentSession session, Guid taskId, CancellationToken cancellationToken)
    {
        if (await session.LoadAsync<TaskListItem>(taskId, cancellationToken) is not { } task)
        {
            return;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(
            session, context.OwnerId, cancellationToken);

        IReadOnlyList<TaskDependencyCatchUp.MissingDependency> asked = await TaskDependencyCatchUp.QueueMissingAsync(
            session, task.ProjectId, [taskId], context.NodeId, ownerRootFingerprint, DateTimeOffset.UtcNow,
            reMintCooldown: null, cancellationToken);
        foreach (TaskDependencyCatchUp.MissingDependency dependency in asked)
        {
            string dependencyShortId = TaskListCommand.ShortId(dependency.StreamId);
            string namedByShortId = TaskListCommand.ShortId(dependency.NamedByTaskId);
            AnsiConsole.MarkupLineInterpolated(
                $"[blue]Asked[/] for dependency [bold]{dependencyShortId}[/][dim]'s event stream, which task {namedByShortId} names and this node does not hold.[/]");
        }
    }

    /// <summary>The stream this run is about, and — when the ledger is what named it — the project
    /// whose ledger did, which is the project to ask unless <c>--project</c> says otherwise.</summary>
    private sealed record Resolution(Guid StreamId, ProjectDetails? LedgerProject);

    /// <summary>
    /// A full id straight through, then <see cref="TaskIdResolver"/>'s own fragment match against
    /// local tasks (which can only ever resolve to one this node DOES hold, and so lands on the
    /// already-here report above), and finally this project's own ledger records — the one local
    /// source that names a task whose stream is not here, and so the only thing that can resolve
    /// the SHORT id a human actually has in front of them (task 9eb5b245: the full id lived only on
    /// the node that held the task, which is the node they are not sitting at).
    /// </summary>
    private static async Task<Resolution> ResolveAsync(
        IQuerySession session, ILedger ledger, IReadOnlyList<ProjectDetails> eligible, string taskIdOrFragment,
        CancellationToken cancellationToken)
    {
        if (taskIdOrFragment.IsBlank())
        {
            throw new DomainValidationException("h9k task pull needs a task id — pass the full id of the task to pull.");
        }

        if (Guid.TryParse(taskIdOrFragment, out Guid full))
        {
            return new Resolution(full, null);
        }

        try
        {
            return new Resolution(await TaskIdResolver.ResolveAsync(session, taskIdOrFragment, cancellationToken), null);
        }
        catch (DomainNotFoundException)
        {
            // Nothing local matches, which is the ordinary case for this command rather than an
            // error: the ledger is asked next, and only its silence is a refusal.
        }

        IReadOnlyList<LedgerTaskLookup.Match> matches = await LedgerTaskLookup.FindAsync(
            ledger, eligible, taskIdOrFragment, cancellationToken);
        Guid[] distinct = [.. matches.Select(match => match.TaskId).Distinct()];
        return distinct switch
        {
            [Guid single] => new Resolution(single, matches.Count == 1 ? matches[0].Project : null),
            [] => throw new DomainValidationException(
                $"'{taskIdOrFragment}' matches no task on this node, and no task record on any eligible "
                + "project's ledger names one either. Pass the task's full id instead — h9k task show prints "
                + "it on the node that holds the task, and its ledger record is records/<task-id>.yaml. A task "
                + "that was never published has no record for this to find, so its full id is the only way to "
                + "name it."),
            _ => throw new DomainConflictException(
                $"'{taskIdOrFragment}' is ambiguous — {distinct.Length} task records on this project's ledgers "
                + "match it. Use more characters, or pass the full id."),
        };
    }

    /// <summary>
    /// Which project's members get asked: the named one, the one whose ledger names this task, or
    /// this node's only eligible one. The middle case is what keeps a human with several projects
    /// registered from having to know which project a task id belongs to in order to ask for it —
    /// and when no ledger names it, <see cref="EligibleProject.ResolveAsync"/>'s own refusals stand
    /// exactly as they did.
    /// </summary>
    private static async Task<ProjectDetails> ResolveProjectAsync(
        IDocumentSession session, ILedger ledger, IReadOnlyList<ProjectDetails> eligible, string? projectOption,
        Resolution resolution, Guid streamId, CancellationToken cancellationToken)
    {
        const string Purpose = "this task should be pulled into";
        if (projectOption.IsNotBlank())
        {
            return await EligibleProject.ResolveAsync(session, projectOption, Purpose, cancellationToken);
        }

        if (resolution.LedgerProject is { } alreadyNamed)
        {
            return alreadyNamed;
        }

        if (eligible.Count <= 1)
        {
            return await EligibleProject.ResolveAsync(session, null, Purpose, cancellationToken);
        }

        IReadOnlyList<LedgerTaskLookup.Match> matches = await LedgerTaskLookup.FindAsync(
            ledger, eligible, streamId.ToString(), cancellationToken);
        ProjectDetails[] named = [.. matches
            .Where(match => match.TaskId == streamId)
            .Select(match => match.Project)
            .DistinctBy(project => project.Id)];
        return named is [ProjectDetails single]
            ? single
            : await EligibleProject.ResolveAsync(session, null, Purpose, cancellationToken);
    }
}
