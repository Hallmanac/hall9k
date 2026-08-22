using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Record the Jira card a task belongs to, from what Jira says rather than from what anybody
/// claims (backlog 18). This is the observation gate, and it is the whole reason card creation
/// can be handed to an agent at all.
/// <para>
/// An agent that has just created a card knows the key it believes it created. That belief is an
/// argument to this command and never the thing this command writes down: the key is read back
/// through the registered connection first, and what is recorded is the response. A key that was
/// mistyped, hallucinated, or created on a different site does not resolve, and the refusal says
/// so in terms the agent can act on — which is what makes retrying the right move rather than a
/// guess.
/// </para>
/// <para>
/// It is deliberately usable by a human too. Linking a card somebody made by hand is the same
/// act with the same verification, and having one command for both means there is exactly one
/// path by which a task acquires an external reference after creation.
/// </para>
/// </summary>
public sealed class TaskLinkJiraCommand : Hall9kAsyncCommand<TaskLinkJiraCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;

        [CommandArgument(1, "<KEY>")]
        [Description(
            "The Jira card: its key (PROJ-123) or its URL. Hall9k reads it through the registered "
            + "connection before recording anything, so this is a key to be checked rather than a fact "
            + "to be accepted — if it does not resolve, nothing is written and the error says what to "
            + "look at")]
        public string Key { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        JiraWorkItemProvider provider = await WorkItemConnections.JiraProviderAsync(session, cancellationToken);
        JiraIssueKey key = JiraIssueKey.Parse(settings.Key, new Uri(provider.Site));

        // Read before deciding. The read is what this command exists for, and doing it first means
        // a key that does not resolve costs nothing and teaches something, whatever state the task
        // happens to be in. ReadAsync rather than the importer: the adoption gate ("only work a
        // source calls open") is a rule about starting work, and this is recording a card that was
        // created because the work already exists.
        ImportedWorkItem card = await provider.ReadAsync(key, cancellationToken);

        // Fence, and fence after the read rather than before it. Reading the card is a request to
        // somebody else's tenant with a 30-second deadline on it, and every guard below is about
        // the task as it is now: a task read before that call and appended to after it could have
        // been abandoned in between, which is exactly what LinkWorkItem refuses to link. The
        // append carries this version, so a write that landed while the tenant was answering fails
        // the commit rather than being absorbed — including a second link-jira carrying a
        // different key, which would otherwise see a null reference on both sides of the race and
        // put two cards on one task. The design expects agents to retry this command, so that
        // window is a normal Tuesday rather than a thought experiment.
        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (TaskDecider.AlreadyLinkedTo(task, card.Reference))
        {
            // The caller most likely to arrive here is an agent that could not tell whether its
            // first attempt landed. Saying "yes, that is what I have" is the answer that lets it
            // stop rather than escalate.
            AnsiConsole.MarkupLine(
                $"[green]Already linked[/] — task {TaskListCommand.ShortId(taskId)} carries "
                + $"{card.Reference.ToString().EscapeMarkup()}. [dim]Nothing to do.[/]");
            return ExitCodes.Ok;
        }

        // One item, one live task — the same rule --from-issue and --from-jira enforce at adoption,
        // applied to the other door into the same field. TaskDecider.LinkWorkItem only knows about
        // this task, so without this a card another live task already carries could be linked here
        // too, and one card would end up with two sets of runs and two closeout comments. The
        // publication prompt makes that the likely mistake rather than an exotic one: a session
        // told to search for an earlier attempt's card before creating a second can find somebody
        // else's and report the key back in good faith.
        await TaskAddCommand.RefuseSecondAdoptionAsync(session, card.Reference, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        WorkItemLinked linked = TaskDecider.LinkWorkItem(
            task,
            card.Reference,
            card.Title,
            card.Status.ToString(),
            card.ObservedAt,
            DateTimeOffset.UtcNow,
            context.OwnerId);

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, linked);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while {card.Reference} was being read back from Jira, so "
                + "nothing was linked. The card is untouched either way — this command only ever reads "
                + "Jira. Check h9k task show and run h9k task link-jira again if the task should still "
                + "carry it.");
        }

        AnsiConsole.MarkupLine(
            $"[green]Linked[/] task {TaskListCommand.ShortId(taskId)} to "
            + $"{card.Reference.ToString().EscapeMarkup()}: "
            + $"{ExternalText.OneLineMarkup(card.Title)}");
        AnsiConsole.MarkupLine(ObservationMarkup(card));
        if (provider.WebUrl(card.Reference) is { } url)
        {
            AnsiConsole.MarkupLine($"[dim]  {url.ToString().EscapeMarkup()}[/]");
        }

        await WarnIfOffTheBoardAsync(session, task, card.Reference, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What was observed, and when, said as the one reading it is: Hall9k does not watch the card
    /// afterwards, so a status with no stamp beside it would read as the card's current state.
    /// <para>
    /// The status goes through <see cref="ExternalText"/> because it is a Jira administrator's
    /// text rather than the platform's, and this command reaches it without the adoption gate in
    /// the way: a card whose status has no category maps to no rule, so
    /// <see cref="WorkItemStatus.Unmapped"/> keeps the tenant's word verbatim, which is right for
    /// the record and unsafe for a terminal. Origin incident (2026-08-22): the pre-PR review of
    /// this branch found this line escaped for Spectre's markup and never sanitised, which
    /// neutralises brackets and not control characters.
    /// </para>
    /// </summary>
    internal static string ObservationMarkup(ImportedWorkItem card) =>
        $"[dim]  {ExternalText.OneLineMarkup(card.Status.ToString())} when read at {card.ObservedStamp}; "
        + "Hall9k took one reading and does not watch the card afterwards.[/]";

    /// <summary>
    /// Say so when the card landed somewhere other than the project's bound board — and say it
    /// rather than refuse it.
    /// <para>
    /// The binding is a default, not a law, and that follows from the design this feature is
    /// built on: the platform deliberately does not model card semantics, and routing rules live
    /// in the project's own skills. A team whose skill says "support requests go to SUP, dev
    /// tasks go to DEV" is doing exactly what it was asked to do when a card lands off the bound
    /// board, and refusing the link would leave a real card unlinked and an agent stuck against a
    /// rule the platform had no business having.
    /// </para>
    /// <para>
    /// A wrong board is still worth a human's attention, though, because the other cause is an
    /// agent that filed a card somewhere nobody is looking. So it is recorded either way and
    /// pointed at once.
    /// </para>
    /// <para>
    /// The board it compares is the one in the reference that was just recorded, not the one in
    /// the key that was asked for. Jira moves a card between projects by giving it a new key and
    /// keeps the old key resolving, so the provider deliberately prefers the tenant's canonical
    /// answer — and a note about a board the task does not carry would send a human looking for a
    /// mismatch that is not there, or stay quiet about one that is.
    /// </para>
    /// </summary>
    private static async Task WarnIfOffTheBoardAsync(
        IQuerySession session, TaskAggregate task, ExternalReference recorded, CancellationToken cancellationToken)
    {
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project?.JiraProjectKey is not { HasValue: true } bound
            || !JiraIssueKey.TryParseBareKey(recorded.Reference, out JiraIssueKey card)
            || bound.Value == card.Project.Value)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]  Note:[/] [dim]{project.Name.EscapeMarkup()} is bound to board "
            + $"{bound.Value.EscapeMarkup()} and this card is on {card.Project.Value.EscapeMarkup()}. "
            + "That is fine when the project's own routing rules put it there; it is worth a look if "
            + "nobody meant to.[/]");
    }
}
