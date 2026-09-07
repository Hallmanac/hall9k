using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The dispatch trigger (Decisions Log #34). Publishing says the task is ready; assigning
/// says it should run now, and on whose nodes. It is always a human's explicit act — the
/// platform never assigns on its own.
/// </summary>
public sealed class TaskAssignCommand : Hall9kAsyncCommand<TaskAssignCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "[OWNER]")]
        [Description(
            "Owner whose nodes may claim the task: their name, an unambiguous fragment of it or of "
            + "their email, or their id. Omit it only when the platform has exactly one owner, "
            + "which is then who it goes to")]
        public string? Owner { get; init; }

        [CommandOption("--take")]
        [Description(
            "In a project whose claim gate is tracker-assignee, take the linked Jira card or GitHub "
            + "issue for this install's own tracker identity as part of assigning, so one command "
            + "moves the tracker and the board together and the gate then passes on its own. Reads "
            + "the item fresh first and writes ONLY when it shows no assignee: an item somebody "
            + "else holds is refused (exit 70) and nothing is written, and there is no flag that "
            + "takes one from another person. Already yours records the observation and proceeds. "
            + "The write is a FIELD UPDATE and never a transition — the item's status is not "
            + "touched — but be aware that a team's own automation (a Jira board rule, a GitHub "
            + "workflow) may react to an assignment. Without this flag an interactive run offers "
            + "the same take on an unassigned item, and a non-interactive one warns and proceeds "
            + "without writing anything")]
        public bool Take { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        OwnerDetails owner = settings.Owner.IsNotBlank()
            ? await OwnerResolver.ResolveAsync(session, settings.Owner, cancellationToken)
            : await OwnerResolver.SoleOwnerAsync(session, cancellationToken)
                ?? throw new DomainValidationException(
                    "More than one owner is registered, so who this task is for cannot be inferred. "
                    + "Name them: h9k task assign <id> <owner>");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskAssigned assigned = await AppendAsync(session, task, owner.Id, context.OwnerId, cancellationToken);

        // Composed above, committed below, and the tracker written to in between — the order is the
        // whole of "one command moves the tracker and the board together". TaskDecider.Assign has
        // already refused a task that cannot be assigned at all (a draft, an abandoned one) by the
        // time anything is written to somebody's board, and the take is the only part of this
        // command that can refuse the assignment: it throws before the save, so a take that could
        // not have the item leaves the task exactly as it was. Everything else about the gate is
        // read after the commit instead, because the tracker is the go signal and a task waiting in
        // the queue is the design (Decisions Log #142, #143).
        (TrackerTake? take, TrackerClaimDecision? decision) = await TakeBeforeAssigningAsync(
            store, session, task, settings.Take, trackerAssignmentTake: null, Offer(), cancellationToken);

        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"task-assigned:{taskId}", cancellationToken);

        await AnnounceAsync(assigned, owner, session, cancellationToken, task.StackedOnTaskId);
        await ReportTrackerAsync(store, session, task, take, decision, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The take, if this run is one that takes: <c>--take</c> outright, or an interactive run in a
    /// gated project offering it on an item the tracker shows assigned to nobody (idea 64c75e43,
    /// Decisions Log #143). Returns the take when one happened, or the read it was decided from
    /// when it did not, so <see cref="ReportTrackerAsync"/> can speak once about an answer read
    /// once.
    /// <para>
    /// <c>--take</c> on a project whose gate is off, or on a task with no gated item, is refused
    /// rather than quietly ignored: what the flag asks for is that the gate pass on its own, and
    /// with no gate there is nothing to pass — writing to somebody's tracker anyway would be this
    /// command doing more than it was asked, on an external service, on a guess about intent.
    /// </para>
    /// <para>
    /// Best-effort about the project itself for the same reason
    /// <see cref="WarnIfTrackerHoldsAsync"/> is: a task whose project document has gone missing is
    /// a record disagreeing with itself, and the assignment is not the place to fail over it — with
    /// one exception, an explicit <c>--take</c>, which cannot be honoured without knowing the
    /// project's gate and its repository path and so says so instead of proceeding as if the flag
    /// had not been passed.
    /// </para>
    /// </summary>
    /// <param name="offer">
    /// Whether to take an unassigned item, asked of the human at the terminal — null when there is
    /// nobody to ask, which is exactly what makes a non-interactive run warn and proceed rather
    /// than write. Resolved by the caller rather than here, so the one place that consults the
    /// terminal is the one place that owns it, and so a test can pin the offer, its acceptance and
    /// its decline without a console.
    /// </param>
    internal static async Task<(TrackerTake? Take, TrackerClaimDecision? Decision)> TakeBeforeAssigningAsync(
        IDocumentStore store,
        IQuerySession session,
        TaskAggregate task,
        bool take,
        TrackerAssignmentTake? trackerAssignmentTake,
        TrackerClaimCheck.TakeOffer? offer,
        CancellationToken cancellationToken)
    {
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
        {
            return take
                ? throw new DomainNotFoundException(
                    $"--take needs this task's project to know whether its claim gate is on and where its "
                    + $"repository is, and no project {task.ProjectId} is recorded on this install. Assign "
                    + "without --take, or repair the project record first (h9k project list).")
                : (null, null);
        }

        if (!TrackerClaimGate.Gates(project.ClaimGate, task.ExternalReference))
        {
            return take
                ? throw new DomainValidationException(
                    "--take assigns the linked Jira card or GitHub issue to this install so this "
                    + $"project's claim gate passes on its own, and {WhyNotGated(project, task)} There is "
                    + "nothing for a take to unlock here, and Hall9k does not write to a tracker it was "
                    + $"not asked to: assign without --take{NotGatedRemedy(project, task)}.")
                : (null, null);
        }

        if (take)
        {
            return (
                await TrackerClaimCheck.TakeOrRefuseAsync(
                    store, task.Id, project, task.ExternalReference?.ToString(), trackerAssignmentTake,
                    cancellationToken),
                null);
        }

        return offer is null
            ? (null, null)
            : await TrackerClaimCheck.OfferOrTakeAsync(
                store, task.Id, project, task.ExternalReference?.ToString(), trackerAssignmentTake, offer,
                cancellationToken);
    }

    /// <summary>
    /// The offer as a human at a terminal answers it, or null when there is no terminal — the one
    /// place this command consults the console about the take, so
    /// <see cref="TakeBeforeAssigningAsync"/> can be exercised in either mode without one.
    /// <para>
    /// The question names the item and the tracker, and says what saying yes buys, because that is
    /// what makes it answerable: a bare "take it?" leaves a human guessing whether it writes to
    /// their team's board. It defaults to no, the same default
    /// <c>h9k task publish</c>'s own assignment offer takes — a write to somebody else's system is
    /// never the answer a bare Enter should give.
    /// </para>
    /// </summary>
    private static TrackerClaimCheck.TakeOffer? Offer() =>
        AnsiConsole.Profile.Capabilities.Interactive
            ? decision => AnsiConsole.Confirm(
                $"{decision.Tracker.EscapeMarkup()} shows {decision.Item.EscapeMarkup()} assigned to "
                + $"nobody. Assign it to you on {decision.Tracker.EscapeMarkup()} now, so this project's "
                + "claim gate passes and stops holding the task?",
                defaultValue: false)
            : null;

    /// <summary>
    /// Which half of the gate's own applicability rule turned <c>--take</c> down, so the refusal
    /// names the actual reason rather than both. The reference is relayed text on the way out, the
    /// same treatment <see cref="TrackerClaimDecision.Item"/> gives the identical field on every
    /// other surface of this feature: what a task carries is whatever <c>h9k task link-jira</c> was
    /// handed, so a newline or a bidirectional override in it can garble what a refusal appears to
    /// say (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    private static string WhyNotGated(ProjectDetails project, TaskAggregate task) =>
        project.ClaimGate != ClaimGate.TrackerAssignee
            ? $"project {project.Name}'s claim gate is off."
            : task.ExternalReference is { } reference
                ? $"this task's linked item ({ExternalText.OneLine(reference.ToString())}) is not one the "
                    + "gate applies to — only a Jira card and a GitHub issue are."
                : "this task has no linked Jira card or GitHub issue at all.";

    /// <summary>
    /// The remedy that matches the reason <see cref="WhyNotGated"/> just gave, and only that one:
    /// telling a human to turn on a gate that is already on — the case where the gate is
    /// tracker-assignee and the task simply has nothing linked — would point them at the one
    /// setting that cannot possibly help (self-review, this session: the refusal named both halves
    /// of the rule and then offered a single remedy for the wrong one).
    /// </summary>
    private static string NotGatedRemedy(ProjectDetails project, TaskAggregate task) =>
        project.ClaimGate != ClaimGate.TrackerAssignee
            ? $", or turn the gate on with h9k project set {project.Name} --claim-gate tracker-assignee"
            : task.ExternalReference is null
                ? ", or link the item first (h9k task link-jira <id> <KEY>, h9k task link-issue <id> <ISSUE>) "
                    + "and take it then"
                : string.Empty;

    /// <summary>
    /// What the tracker had to say, once the assignment has landed: the take's own line when one
    /// happened, the read's warning when the gate was read but nothing taken, and otherwise a fresh
    /// read — the behaviour every path had before <c>--take</c> existed.
    /// </summary>
    private static async Task ReportTrackerAsync(
        IDocumentStore store,
        IQuerySession session,
        TaskAggregate task,
        TrackerTake? take,
        TrackerClaimDecision? decision,
        CancellationToken cancellationToken)
    {
        // A take has already had the whole conversation with the tracker, so nothing else here
        // speaks. Its line is checked rather than assumed non-empty: only Taken and AlreadyMine
        // have a sentence, and a take that passed with neither — NotGated, which cannot reach here
        // through this door — must not print a bare green blank line if it ever does
        // (self-review, this session).
        if (take is { } taken)
        {
            if (taken.TookLine.IsNotBlank())
            {
                AnsiConsole.MarkupLine($"[green]{taken.TookLine.EscapeMarkup()}[/]");
            }

            return;
        }

        if (decision is { } read)
        {
            await TrackerClaimCheck.WarnAndRecordAsync(
                store, task.Id, task.ExternalReference?.ToString(), read, cancellationToken);
            return;
        }

        await WarnIfTrackerHoldsAsync(store, session, task, trackerClaimGate: null, cancellationToken);
    }

    /// <summary>
    /// The claim gate, read after the assignment has landed and never allowed to refuse it (idea
    /// 64c75e43): the tracker's assignment is the go signal, so assigning is still the right act
    /// — it puts the task in the queue the gate lets it out of the moment the item is assigned.
    /// Shared with <c>h9k task publish --assign</c>, which queues work the same way.
    /// <para>
    /// Best-effort about the project itself: a task whose project document has gone missing is a
    /// record disagreeing with itself, and failing the assignment over it would be this command
    /// failing for a reason that has nothing to do with what it was asked to do — the dispatcher's
    /// own door reads the same gate again before anything claims.
    /// </para>
    /// </summary>
    internal static async Task WarnIfTrackerHoldsAsync(
        IDocumentStore store,
        IQuerySession session,
        TaskAggregate task,
        TrackerClaimGate? trackerClaimGate,
        CancellationToken cancellationToken)
    {
        if (await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken) is { } project)
        {
            await TrackerClaimCheck.WarnAndRecordAsync(
                store, task.Id, project, task.ExternalReference?.ToString(), trackerClaimGate, cancellationToken);
        }
    }

    /// <summary>
    /// Appends the assignment onto an open session (the caller saves), so h9k task publish can
    /// offer assignment in the same transaction it publishes in. Dependencies are read here
    /// rather than passed in: where the task lands — Queued or Blocked — is decided by whether
    /// each blocker has reached true closeout at this moment.
    /// </summary>
    internal static async Task<TaskAssigned> AppendAsync(
        IDocumentSession session,
        TaskAggregate task,
        Guid assignedOwnerId,
        Guid assignedByOwnerId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
            session, task.BlockedBy, cancellationToken);
        TaskAssigned assigned = TaskDecider.Assign(
            task, assignedOwnerId, dependencies, DateTimeOffset.UtcNow, assignedByOwnerId);
        session.Events.Append(task.Id, assigned);
        return assigned;
    }

    /// <summary>
    /// Says which of the two landings happened, and what the blocked one is waiting on.
    /// <paramref name="stackedOnTaskId"/> is the task's own declared stacked edge, null on every
    /// unstacked task: what decides whether a blocker listed below is met at its Delivered or only
    /// at its merge, and therefore which sentence honestly describes what unblocks this task (task:
    /// a stacked pull-request edge exists as an explicit opt-in dependency).
    /// </summary>
    internal static async Task AnnounceAsync(
        TaskAssigned assigned, OwnerDetails owner, IQuerySession session, CancellationToken cancellationToken,
        Guid? stackedOnTaskId = null)
    {
        string shortId = TaskListCommand.ShortId(assigned.Id);
        if (assigned.UnmetDependencies.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[green]Task {shortId} assigned to {owner.Name.EscapeMarkup()}[/] — queued; "
                + "the next dispatch cycle on one of their nodes claims it.");
            return;
        }

        IReadOnlyList<TaskDependency> unmet = await TaskDependencyQuery.LoadAsync(
            session, assigned.UnmetDependencies, cancellationToken);
        AnsiConsole.MarkupLine(
            $"[yellow]Task {shortId} assigned to {owner.Name.EscapeMarkup()}[/] — blocked on "
            + $"{assigned.UnmetDependencies.Count} dependency(ies) that have not closed out:");
        // The lifecycle word rather than the persisted one (Decisions Log #66), and the mark that
        // word depends on, exactly as h9k task show pairs them. A blocker whose pull request is
        // still open now reads Delivered here instead of claiming a Done the sentence above it
        // has just denied. One case survives the word alone: a blocker closed by hand with no
        // pull request is spelt Done, because there was never a merge to observe, while the
        // dependency rule still refuses it — so the mark is what carries that disagreement, and
        // printing the word without it is printing half the sentence.
        foreach (TaskDependency dependency in unmet)
        {
            AnsiConsole.MarkupLine(
                $"  {TaskStatusComposer.DependencyMark(dependency, stackedOnTaskId)} "
                + $"[dim]{TaskListCommand.ShortId(dependency.Id)}[/] "
                + $"{TaskListCommand.Truncate(ExternalText.OneLine(dependency.Objective), 60).EscapeMarkup()} "
                + $"({TaskStatusComposer.State(dependency).Markup})"
                + (stackedOnTaskId == dependency.Id ? " [blue]— stacked on this[/]" : string.Empty));
        }

        // Which bar the last one has to clear is exactly what the stacked edge changes, so the
        // closing line has to say which: a stacked parent releases this task at its Delivered, an
        // ordinary blocker only at its merge.
        AnsiConsole.MarkupLine(unmet.Any(dependency => stackedOnTaskId == dependency.Id)
            ? "[dim]It queues itself the moment the last one clears — its stacked parent at Delivered "
              + "(pull request open), any other blocker at its merge — nothing else to do.[/]"
            : "[dim]It queues itself the moment the last one's pull request merges — nothing else to do.[/]");
    }
}
