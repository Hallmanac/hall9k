using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
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
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The readiness gate (Decisions Log #34). Publishing is the quality decision, not the go
/// signal: it says the contract is complete and the dependency graph is sane, after which the
/// task is immutable and assignable. Starting it is a separate, explicit act.
/// </summary>
public sealed class TaskPublishCommand : Hall9kAsyncCommand<TaskPublishCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--assign [OWNER]")]
        [Description(
            "Assign the task in the same breath, so it dispatches: the owner's name, an unambiguous "
            + "fragment, or their id — or the bare flag when the platform has exactly one owner. This "
            + "is the same explicit TaskAssigned event h9k task assign appends, never a silent one")]
        public FlagValue<string> Assign { get; init; } = new();

        [CommandOption("--no-assign")]
        [Description(
            "Publish and stop there, without being asked about assignment. Use it in scripts: an "
            + "interactive terminal is otherwise offered the single-owner assignment as a convenience")]
        public bool NoAssign { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Assign.IsSet && settings.NoAssign)
        {
            throw new DomainValidationException("--assign and --no-assign say opposite things; pass one.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId} names a project that is not registered.");

        // The whole reachable chain, not just the first hop: a cycle three tasks away is still
        // a cycle this task could never run inside.
        TaskDependencyGraph graph = await TaskDependencyQuery.LoadGraphAsync(
            session, task.BlockedBy, cancellationToken);

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskPublished published = TaskDecider.Publish(task, graph, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(taskId, published);
        task.Apply(published);

        // Resolve the assignee and commit before announcing anything. Both steps can still throw
        // — a bare --assign with several owners registered, a name that matches nobody — and the
        // session is then disposed unsaved, leaving the task a Draft. Announcing the publish
        // first would tell a human (or an agent reading the message to self-correct) about a
        // state change the failed transaction never made.
        OwnerDetails? assignee = await ChooseAssigneeAsync(session, settings, cancellationToken);
        TaskAssigned? assigned = assignee is null
            ? null
            : await TaskAssignCommand.AppendAsync(session, task, assignee.Id, context.OwnerId, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);

        // The objective is printed whole rather than cut to a column width. This line is the last
        // look a human gets at what they are making assignable, and for an adopted task the
        // objective started life as somebody else's issue title (PLAN.md §3.1a): a long one can
        // keep its real intent past the point a list view would have stopped reading.
        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(
            $"[green]Task {shortId} published[/]: {ExternalText.OneLineMarkup(task.Objective)}");

        // Every published task is tracked automatically: a task adopted with --from-issue or
        // --from-jira already carries a reference (RequestWorkItemPublication and LinkWorkItem
        // both refuse a second one on their own), so this is skipped entirely rather than
        // silently creating nothing — the difference between "adopted" and "declined" is worth
        // seeing on the stream, and a task with a reference already has neither.
        if (task.ExternalReference is null)
        {
            await TrackInBacklogAsync(store, taskId, shortId, task, project, context.OwnerId, cancellationToken);
        }

        if (assignee is null || assigned is null)
        {
            AnsiConsole.MarkupLine(
                $"[dim]It is ready to assign but will not run until you say so:[/] h9k task assign {shortId}");
            return ExitCodes.Ok;
        }

        await Doorbell.RingAsync($"task-assigned:{taskId}", cancellationToken);
        await TaskAssignCommand.AnnounceAsync(assigned, assignee, session, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Backlog: every published task is tracked automatically, per the project's own setting
    /// (h9k project set --backlog). Both branches run in their own store session, after the
    /// publish transaction has already committed — jira dispatches an agent session and cannot
    /// be made to happen inside a CLI invocation at all, and github-issues calls out to gh, which
    /// this repository's own idiom (TaskLinkJiraCommand, CardPublicationEngine) never mixes into
    /// the transaction doing the deciding. Either way a failure here is reported and swallowed:
    /// the task is published either way, and an operator told what to run by hand is better than
    /// a publish that half-failed.
    /// </summary>
    private static async Task TrackInBacklogAsync(
        DocumentStore store,
        Guid taskId,
        string shortId,
        TaskAggregate task,
        ProjectDetails project,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        if (project.BacklogPolicy == BacklogPolicy.Jira)
        {
            TaskPushToJiraCommand.AutoRequestOutcome jiraOutcome;
            try
            {
                jiraOutcome = await TaskPushToJiraCommand.TryAutoRequestAsync(
                    store, taskId, ownerId, cancellationToken);
            }
            catch (DomainException exception)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]  Note:[/] [dim]{project.Name.EscapeMarkup()} tracks its backlog in Jira, but "
                    + $"requesting publication failed: {exception.Message.EscapeMarkup()} Push it by hand "
                    + $"once the task settles:[/] h9k task push-to-jira {shortId}");
                return;
            }

            AnsiConsole.MarkupLine(jiraOutcome == TaskPushToJiraCommand.AutoRequestOutcome.Requested
                ? $"[dim]  {project.Name.EscapeMarkup()} tracks its backlog in Jira — publication "
                    + "requested; the daemon dispatches the session that writes the card.[/]"
                : $"[yellow]  Note:[/] [dim]{project.Name.EscapeMarkup()} tracks its backlog in Jira, but no "
                    + "Jira connection is registered, so nothing was requested. Register one, then push it "
                    + $"by hand:[/] h9k task push-to-jira {shortId}");
            return;
        }

        if (project.BacklogPolicy != BacklogPolicy.GitHubIssues)
        {
            return;
        }

        GitHubWorkItemProvider provider = new();
        ImportedWorkItem issue;
        try
        {
            issue = await provider.CreateAsync(
                new GitHubIssueCreateRequest(
                    RelayedText.WithoutClosingKeywords(ExternalText.OneLine(task.Objective)),
                    GitHubIssueBody.Compose(task.AgentContext, task.AcceptanceCriteria),
                    GitHubIssueBody.Labels(project.BacklogRoutingGuidance),
                    project.RepositoryPath),
                cancellationToken);
        }
        catch (DomainConflictException exception)
        {
            // The issue was created; only reading it back to verify it failed. Advising a
            // by-hand create here (as the branch below does) would file a duplicate.
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]{project.Name.EscapeMarkup()} tracks its backlog in GitHub "
                + $"issues, but {exception.Message.EscapeMarkup()} Link it:[/] h9k task link-issue "
                + $"{shortId} <issue>");
            return;
        }
        catch (DomainException exception)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]{project.Name.EscapeMarkup()} tracks its backlog in GitHub issues, "
                + $"but creating one failed: {exception.Message.EscapeMarkup()} Create it by hand and link "
                + $"it:[/] h9k task link-issue {shortId} <issue>");
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskLinkIssueCommand.LinkOutcome outcome;
        try
        {
            outcome = await TaskLinkIssueCommand.LinkAsync(session, taskId, issue, ownerId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DomainException or EventStreamUnexpectedMaxEventIdException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  Note:[/] [dim]Created {issue.Reference.ToString().EscapeMarkup()} but could not "
                + $"link it: {exception.Message.EscapeMarkup()} Link it by hand:[/] h9k task link-issue "
                + $"{shortId} {issue.Reference.Reference.EscapeMarkup()}");
            return;
        }

        AnsiConsole.MarkupLine(outcome == TaskLinkIssueCommand.LinkOutcome.Linked
            ? $"[dim]  {project.Name.EscapeMarkup()} tracks its backlog in GitHub issues — created and "
                + $"linked {issue.Reference.ToString().EscapeMarkup()}.[/]"
            : $"[dim]  {project.Name.EscapeMarkup()} tracks its backlog in GitHub issues; task {shortId} "
                + "already carried a reference by the time this landed.[/]");
    }

    /// <summary>
    /// Who to assign to, or null for "nobody yet". The flag is an explicit answer either way.
    /// The interactive offer exists only where it cannot be wrong: exactly one owner is
    /// registered, so "assign it" has one possible meaning. With more than one owner it is
    /// never offered — deciding whose nodes run a task is the human's call, and a prompt that
    /// guessed would be the multi-owner mistake IDEA-task-assignment exists to avoid.
    /// </summary>
    private static async Task<OwnerDetails?> ChooseAssigneeAsync(
        IQuerySession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.NoAssign)
        {
            return null;
        }

        if (settings.Assign.IsSet)
        {
            return settings.Assign.Value.IsNotBlank()
                ? await OwnerResolver.ResolveAsync(session, settings.Assign.Value, cancellationToken)
                : await OwnerResolver.SoleOwnerAsync(session, cancellationToken)
                    ?? throw new DomainValidationException(
                        "More than one owner is registered, so a bare --assign cannot say who this task "
                        + "is for. Name them: h9k task publish <id> --assign <owner>");
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return null;
        }

        OwnerDetails? sole = await OwnerResolver.SoleOwnerAsync(session, cancellationToken);
        return sole is not null && AnsiConsole.Confirm(
            $"Assign it to {sole.Name.EscapeMarkup()} now, so it can dispatch?", defaultValue: false)
            ? sole
            : null;
    }
}
