using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What the step after a linear-policy publish came to. <see cref="Linked"/> is whether the task
/// carries a Linear issue afterwards. <see cref="QueueDeferred"/> is true when the caller asked for
/// the go signal on a publish that creates an issue, which waits for the link; <see cref="Queued"/> is
/// that signal once it was appended, null when no issue was linked or the append failed (both said on
/// the terminal already).
/// </summary>
internal sealed record LinearTrackingResult(bool Linked, bool QueueDeferred, TaskAssigned? Queued);

/// <summary>The Linear client and the team it resolved, carried from the check before a publish commits to the create after it.</summary>
internal sealed record LinearPublishTarget(LinearWorkItemProvider Provider, LinearTeam Team);

/// <summary>
/// What <c>h9k task publish</c> does for a project whose backlog policy is linear, behind the same
/// link, create, or untracked choice Jira and GitHub issues use. Two moments, both on the publishing
/// install itself: <see cref="ResolveAsync"/> before the publish commits, and
/// <see cref="TrackAsync"/> after it.
/// <para>
/// The resolution comes first and can refuse, because after the commit nothing can be refused: a
/// publish that will create an issue needs the team's id and its default state, and a node with no
/// Linear connection, a rejected key, or an unreachable Linear is the ordinary case for a teammate's
/// install, so it fails with the task still a Draft and names <c>--untracked</c> as the way through
/// (a task never needs a Linear issue). The create is one <c>issueCreate</c>, sent once and never
/// retried, then read back and linked through the step <c>h9k task link-linear</c> uses. The Linear
/// client and the <see cref="ILedger"/> are parameters so the outcomes run against recorded responses
/// and a fake ledger.
/// </para>
/// </summary>
internal static class LinearPublishTracking
{
    /// <summary>Longer titles are cut here. Linear's own limit is not stated in its documentation, so this is the safe side of GitHub's 256.</summary>
    private const int TitleMaxLength = 255;

    /// <summary>
    /// Resolve the project's team through Linear, or refuse the publish before it commits. Called only
    /// when the publish will create an issue; a publish with <c>--untracked</c>, or for a task that
    /// already carries an issue, never asks Linear anything.
    /// </summary>
    internal static async Task<LinearPublishTarget> ResolveAsync(
        LinearWorkItemProvider? provider, ProjectDetails project, Guid taskId, CancellationToken cancellationToken)
    {
        string shortId = TaskListCommand.ShortId(taskId);
        if (provider is null)
        {
            throw Refusal(shortId, $"this install cannot create the issue. {WorkItemConnections.NoLinearConnection}.");
        }

        if (!project.LinearTeam.HasValue)
        {
            throw Refusal(
                shortId,
                $"{project.Name} has the backlog policy linear but no Linear team bound, so there is nowhere to "
                + $"create the issue. Bind one: h9k project set {project.Name} --linear <TEAM>.");
        }

        LinearTeam? team;
        try
        {
            team = await provider.FindTeamAsync(project.LinearTeam, cancellationToken);
        }
        catch (DomainException exception)
        {
            throw Refusal(shortId, exception.Message);
        }

        return team is null
            ? throw Refusal(
                shortId,
                $"Linear shows no team {project.LinearTeam.Value} through this install's key: the team may not "
                + "exist, or the key may be limited to other teams. Check the team's key in Linear, then rebind: "
                + $"h9k project set {project.Name} --linear <TEAM>.")
            : new LinearPublishTarget(provider, team);
    }

    /// <summary>A refusal to publish, with the way to publish without Linear named last.</summary>
    internal static DomainValidationException Refusal(string shortId, string reason) => new(
        $"Task {shortId} was not published, and nothing was created in Linear: {reason} To publish without a "
        + $"Linear issue, skip tracking for this task deliberately: h9k task publish {shortId} --untracked.");

    /// <summary>
    /// The create, read-back, and link after a publish has committed, the task record on every outcome,
    /// and then the go signal when <paramref name="queueFor"/> names who it is for. The go signal comes
    /// last and only after an issue is linked: dispatch cuts its branch from the identifier, so a queue
    /// that raced ahead of the link would cut <c>no-key</c>, which Linear never links to the issue. A
    /// publish that creates nothing (an untracked task, an already linked one) is not deferred: the
    /// caller appends its go signal in the publish transaction, so <paramref name="queueFor"/> is
    /// passed only for one that creates.
    /// </summary>
    internal static async Task<LinearTrackingResult> TrackAsync(
        DocumentStore store,
        Guid taskId,
        TaskAggregate task,
        TaskPublished published,
        ProjectDetails project,
        BootstrapContext context,
        LinearPublishTarget? target,
        OwnerDetails? queueFor,
        ILedger ledger,
        CancellationToken cancellationToken)
    {
        string shortId = TaskListCommand.ShortId(taskId);
        string name = project.Name.EscapeMarkup();
        bool linked = task.ExternalReference is not null;

        if (published.UntrackedAttested)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  {name} tracks its backlog in Linear, but task {shortId} was published --untracked: "
                + "no issue will be created or linked for it.[/]");
        }
        else if (!published.NoExistingItemAttested)
        {
            // The decider asks for an attestation of every task that carries no item and has no
            // publication outstanding, so nothing here is to be created: the task already carries an
            // issue, or a publication request made earlier by hand is still outstanding.
            if (task.PendingPublicationProvider is { } pending)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]  {name} tracks its backlog in Linear, but task {shortId} already has a "
                    + $"{pending.Value.EscapeMarkup()} publication request outstanding; nothing was created.[/]");
            }
        }
        else
        {
            linked = await CreateAndLinkAsync(
                store, taskId, shortId, task, project, context, target
                    ?? throw new InvalidOperationException(
                        "A publish that creates a Linear issue resolves its team before it commits."),
                cancellationToken);
        }

        // The record goes in after the link, never as part of the create body: the branch name it
        // carries renders through the project's branch template, whose {key} token is this very
        // issue's identifier. And it goes in on every outcome, an untracked task and a failed create
        // included: a task with no tracker item gets a record too (Brian, 2026-09-13), and without
        // one its claim has no holder lock to take.
        await TaskPublishCommand.WriteRecordAsync(
            store, taskId, shortId, project, context, criteriaChanged: false, ledger, cancellationToken);

        if (queueFor is null)
        {
            return new LinearTrackingResult(linked, QueueDeferred: false, Queued: null);
        }

        if (!linked)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Task {shortId} is published but not queued:[/] [dim]queueing waits for the Linear "
                + "issue, because dispatch cuts its branch from the issue's identifier and would cut "
                + $"no-key without it. Link one, then queue it:[/] h9k task link-linear {shortId} "
                + $"<identifier>[dim], then[/] h9k task queue {shortId}");
            return new LinearTrackingResult(linked, QueueDeferred: true, Queued: null);
        }

        return new LinearTrackingResult(
            linked, QueueDeferred: true,
            await QueueAfterLinkAsync(store, taskId, queueFor, context, shortId, cancellationToken));
    }

    /// <summary>
    /// The go signal, appended once the link is on the stream. Its own transaction, on a re-read task:
    /// the publish already committed without it, and the aggregate the caller holds predates the link.
    /// A failure is reported and leaves the task published and not queued, which is safe and visible.
    /// </summary>
    private static async Task<TaskAssigned?> QueueAfterLinkAsync(
        DocumentStore store,
        Guid taskId,
        OwnerDetails queueFor,
        BootstrapContext context,
        string shortId,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        try
        {
            TaskAggregate current = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");
            TaskAssigned assigned = await TaskAssignCommand.AppendAsync(
                session, current, queueFor, context.OwnerId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            return assigned;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]Task {shortId} is published and linked, but queueing it failed: "
                + $"{ExternalText.OneLineMarkup(exception.Message)} Queue it:[/] h9k task queue {shortId}");
            return null;
        }
    }

    private static async Task<bool> CreateAndLinkAsync(
        DocumentStore store,
        Guid taskId,
        string shortId,
        TaskAggregate task,
        ProjectDetails project,
        BootstrapContext context,
        LinearPublishTarget target,
        CancellationToken cancellationToken)
    {
        string name = project.Name.EscapeMarkup();
        string oneLineObjective = ExternalText.OneLine(task.Objective);
        string title = RelayedText.Truncate(oneLineObjective, TitleMaxLength);
        // A cut title loses everything past the cut unless the description carries the rest.
        string? truncatedObjective = title.Length < oneLineObjective.Length ? task.Objective : null;

        LinearIssueCreation creation;
        try
        {
            creation = await target.Provider.CreateIssueAsync(
                new LinearIssueCreateRequest(
                    target.Team.Id,
                    target.Team.DefaultIssueStateId,
                    title,
                    GitHubIssueBody.Compose(task.AgentContext, task.AcceptanceCriteria, truncatedObjective)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing in the provider is expected to throw past its own classification, but if it does
            // the request may already have left, so this is the unknown outcome and never the refused one.
            creation = new LinearIssueCreation(
                LinearCreateOutcome.Unknown,
                null,
                new DomainValidationException(exception.Message));
        }

        if (creation.Outcome != LinearCreateOutcome.Created || creation.Identifier is not { } created)
        {
            string reason = ExternalText.OneLineMarkup(creation.Failure?.Message ?? "no reason was given");
            AnsiConsole.MarkupLine(creation.Outcome == LinearCreateOutcome.Refused
                ? $"[yellow]  Note:[/] [dim]{name} tracks its backlog in Linear, but Linear refused to create the "
                    + $"issue: {reason} Nothing was created. Create it by hand in team "
                    + $"{target.Team.Key.EscapeMarkup()}, then link it:[/] h9k task link-linear {shortId} <identifier>"
                : $"[yellow]  Note:[/] [dim]{name} tracks its backlog in Linear, but whether the issue was created "
                    + $"is unknown: {reason} It may exist, so do not create another before searching team "
                    + $"{target.Team.Key.EscapeMarkup()} for it. Link it if it is there:[/] h9k task link-linear "
                    + $"{shortId} <identifier>[dim], and create it by hand only if it is not.[/]");
            return false;
        }

        string identifier = created.Value;
        string byHand = $"h9k task link-linear {shortId} {identifier.EscapeMarkup()}";
        LinearIssueAttempt attempt;
        try
        {
            attempt = await target.Provider.TryReadIssueAsync(created, linkedTask: null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            attempt = new LinearIssueAttempt(null, new DomainValidationException(exception.Message), TrackerReadFailure.Item);
        }

        if (attempt.Read is not { } read)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]Created {identifier.EscapeMarkup()} in Linear but could not read it "
                + $"back: {ExternalText.OneLineMarkup(attempt.Failure?.Message ?? "no reason was given")} Link it by hand:[/] "
                + byHand);
            return false;
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskLinkLinearCommand.LinkResult result;
        try
        {
            result = await TaskLinkLinearCommand.LinkAsync(session, taskId, read, context.OwnerId, cancellationToken);
            if (result.Outcome == TaskLinkLinearCommand.LinkOutcome.Linked)
            {
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]Created {identifier.EscapeMarkup()} in Linear but could not link it: "
                + $"{ExternalText.OneLineMarkup(exception.Message)} Link it by hand:[/] {byHand}");
            return false;
        }

        AnsiConsole.MarkupLine(result.Outcome == TaskLinkLinearCommand.LinkOutcome.Linked
            ? $"[dim]  {name} tracks its backlog in Linear; created and linked {read.Item.Reference.ToString().EscapeMarkup()}.[/]"
            : $"[dim]  {name} tracks its backlog in Linear; task {shortId} already carried a reference by the "
                + "time this landed.[/]");
        if (target.Provider.WebUrl(read.Item.Reference) is { } url)
        {
            AnsiConsole.MarkupLine($"[dim]  {url.ToString().EscapeMarkup()}[/]");
        }

        return true;
    }
}
