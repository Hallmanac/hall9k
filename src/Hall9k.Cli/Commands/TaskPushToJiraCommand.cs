using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Ask for this task to be written up as a Jira card (backlog 18). It records the request and
/// rings the doorbell; the daemon dispatches an agent session that composes the card payload.
/// <para>
/// The platform does not decide the card's content itself, and that is the central design
/// decision rather than a staging limitation. Reading Jira is configuration-agnostic — a GET
/// answers the same shape however exotic a project's issue types are — but writing it is
/// configuration all the way down: which type a "dev task" is, which fields are mandatory, which
/// board a support request is routed to. Those rules belong to the organisation, and a team that
/// has them already has them written down. So the session is dispatched into the project's
/// repository, where its own Claude skills are, to work out what the card should look like — but
/// it makes no Jira call itself (Decisions Log #102): it composes a payload and submits it through
/// <c>h9k task write-jira</c>, which is the sole executor of every Jira write.
/// </para>
/// </summary>
public sealed class TaskPushToJiraCommand : Hall9kAsyncCommand<TaskPushToJiraCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);

        // A connection is required even though this command makes no Jira call, because the
        // dispatched session's prompt needs the site to compose the payload against. The write
        // itself is verified later by h9k task write-jira, not by this command.
        _ = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken)
            ?? throw new DomainNotFoundException(
                "No Jira connection is registered, so the dispatched session would have no site to "
                + "compose the card payload for (backlog 18). Register one first: h9k connection add "
                + "jira --site https://your-org.atlassian.net --email you@example.com");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        (TaskAggregate task, ProjectDetails project) = await RequestAsync(
            session, taskId, context.OwnerId, cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(
            $"[blue]Publication requested[/] for task {shortId}: "
            + $"{ExternalText.OneLineMarkup(task.Objective)}");
        AnsiConsole.MarkupLine(project.JiraProjectKey.HasValue
            ? $"[dim]  The agent is told to file it under {project.JiraProjectKey.Value.EscapeMarkup()}, "
              + "and to follow the project's own rules if they say otherwise.[/]"
            : $"[dim]  No board is bound to {project.Name.EscapeMarkup()}, so the agent works out where "
              + $"the card belongs from the project's own skills. Bind one with: h9k project set "
              + $"{project.Name.EscapeMarkup()} --jira PROJ[/]");
        AnsiConsole.MarkupLine(
            "[dim]  The daemon dispatches the session; it composes the card and submits it through "
            + $"h9k task write-jira, which is the sole executor of the write. Watch it with:[/] h9k task show {shortId}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// What asking for a Jira publication automatically (a project's backlog policy, not a human
    /// typing push-to-jira) came to — the human command's own missing-connection case is a
    /// refusal that stops a whole invocation, but a publish that already succeeded must not be
    /// undone by a connection nobody has registered yet, so this reports the gap instead of
    /// throwing through it.
    /// </summary>
    internal enum AutoRequestOutcome
    {
        Requested,
        NoJiraConnection,
    }

    /// <summary>
    /// The push-to-jira request, made on the task's behalf by <see cref="TaskPublishCommand"/>
    /// when a project's backlog policy is jira, rather than by a human typing the command. Every
    /// rule is the same one <see cref="ExecuteAsync"/> enforces — one card per task
    /// (<see cref="TaskDecider.RequestWorkItemPublication"/> refuses a second one on its own) —
    /// this only changes what happens when there is no Jira connection to ask: a human running
    /// the command by hand gets a refusal naming the fix, and a project that has never registered
    /// one gets told once, at publish, and can push manually later once it has.
    /// </summary>
    internal static async Task<AutoRequestOutcome> TryAutoRequestAsync(
        IDocumentStore store, Guid taskId, Guid requestedByOwnerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        if (await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken) is null)
        {
            return AutoRequestOutcome.NoJiraConnection;
        }

        await RequestAsync(session, taskId, requestedByOwnerId, cancellationToken);
        return AutoRequestOutcome.Requested;
    }

    /// <summary>
    /// The append <see cref="ExecuteAsync"/> and <see cref="TryAutoRequestAsync"/> both make, once
    /// each has decided a request belongs to be sent — a human command refuses outright with no
    /// Jira connection registered, the automatic trigger reports the gap instead, and everything
    /// past that point is one path, factored once so a later change to the fence discipline (see
    /// the comment this carried forward) cannot land on one caller and not the other.
    /// </summary>
    private static async Task<(TaskAggregate Task, ProjectDetails Project)> RequestAsync(
        IDocumentSession session, Guid taskId, Guid requestedByOwnerId, CancellationToken cancellationToken)
    {
        // Fence, and fence here rather than at the top: the append below carries this version, so
        // anything landing on the task while this command was doing its own reads fails the commit
        // instead of being absorbed. The write that matters is h9k task write-jira, which an agent
        // may be running at any moment, and whose create success appends WorkItemLinked (through
        // JiraWriteCoordinator) as it lands. Read unfenced, the guards in RequestWorkItemPublication
        // see a task with no reference on both sides of that race, the request appends after the
        // link, and the daemon then dispatches a session to write a card for work that already
        // carries one. Bootstrap alone can shell out to git and gh above, so the window is a real
        // one rather than an instant.
        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId} names a project that is not registered.");

        WorkItemPublicationRequested requested = TaskDecider.RequestWorkItemPublication(
            task, WorkItemProvider.Jira, project.JiraProjectKey, DateTimeOffset.UtcNow, requestedByOwnerId);

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, requested);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while this request was being prepared, so nothing was "
                + "requested. The likeliest change is a card being linked to it. Check it with "
                + $"h9k task show {taskId} and run h9k task push-to-jira again if it still needs one.");
        }

        await Doorbell.RingAsync($"publication-requested:{taskId}", cancellationToken);
        return (task, project);
    }
}
