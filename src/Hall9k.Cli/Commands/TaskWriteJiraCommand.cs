using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The write surface (Brian's design, 2026-08-28, superseding the agent-mediated-only ruling;
/// the executor's own transport moved off the Atlassian CLI (twg) onto hall9k's own REST client,
/// Decisions Log #114): the one door through which a composed Jira create, update, or comment
/// reaches Jira at all. Composition — the issue type, the built-in and custom fields, the comment
/// text — is an agent's or an operator's judgment; hall9k is the sole executor, which is what this
/// command actually does: validate the payload against the executor's own guardrails (no
/// transition, no close, regardless of who composed it), record the intent with the full payload
/// before anything is sent, execute it against the Jira Cloud REST API, verify by reading the item
/// back, and record the outcome including the returned key.
/// <para>
/// A rejected credential is a handled state rather than a crash: the write is recorded as pending
/// on this task, a needs-you attention item tells the operator to refresh the registered
/// connection's API token (<c>h9k connection add jira</c>), and the daemon's retry sweep finishes
/// the identical request once that succeeds — nothing here has to be composed or submitted twice.
/// </para>
/// <para>
/// This is also what publish-time card creation under the jira backlog policy now goes through:
/// the agent session <c>h9k task push-to-jira</c> dispatches composes the payload, then submits it
/// here instead of writing to Jira itself, so a card hall9k created and one an agent adopted by
/// hand are recorded through the identical path (<see cref="TaskLinkJiraCommand"/> for the latter).
/// </para>
/// </summary>
public sealed class TaskWriteJiraCommand : Hall9kAsyncCommand<TaskWriteJiraCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--op <OPERATION>")]
        [Description(
            "What to do: create, update, or comment. A transition or a close is refused whatever is "
            + "passed here — which workflow state a card belongs in is this team's own configuration, "
            + "done in Jira directly, never a write hall9k performs.")]
        public string Operation { get; init; } = string.Empty;

        [CommandOption("--file <PATH>")]
        [Description(
            "Path to the composed payload, a JSON object carrying (as needed) workItemType, fields "
            + "(an object of field name to value — use the customfield_* id Jira's own field metadata "
            + "reports, not a display name), comment, projectKey (only for --op create, when the project's own routing "
            + "rules say a different board than the one bound with h9k project set --jira), and format "
            + "(\"markdown\" or \"plain\" for how the description or comment text is "
            + "written; defaults to markdown, since that is what most card-authoring skills produce)")]
        public string File { get; init; } = string.Empty;

        [CommandOption("--issue <KEY>")]
        [Description(
            "The Jira item to update or comment on. Optional when the task already carries a linked "
            + "item — that item is used automatically — but takes precedence over it when both are "
            + "present, so this also targets a different item than the one linked, deliberately. Not "
            + "used for --op create.")]
        public string? Issue { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.File.IsBlank())
        {
            throw new DomainValidationException("--file names the composed payload to submit.");
        }

        if (!File.Exists(settings.File))
        {
            throw new DomainValidationException($"No file at {settings.File} to read the composed payload from.");
        }

        JiraWriteOperation operation = JiraWriteOperation.Parse(settings.Operation);
        JiraWritePayload payload = JiraWritePayload.FromJson(await File.ReadAllTextAsync(settings.File, cancellationToken));

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(details.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId} names a project that is not registered.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        // Every other Jira write surface (link-jira, from-jira, push-to-jira,
        // CardPublicationEngine) refuses outright with no connection registered, and this is the
        // sole executor of every Jira write, so it holds to the same rule: a card filed, verified,
        // and recorded against the wrong tenant is worse than a refusal naming the fix.
        JiraWriteExecutor executor = await WorkItemConnections.JiraWriteExecutorAsync(session, cancellationToken);

        JiraWriteAttemptResult result = await JiraWriteCoordinator.SubmitAsync(
            session, taskId, operation, settings.Issue, payload, project.JiraProjectKey, context.OwnerId,
            executor, cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        switch (result.Outcome)
        {
            case JiraWriteOutcome.Succeeded:
                AnsiConsole.MarkupLine(
                    $"[green]{operation.Value}[/] recorded for task {shortId}: {result.IssueKey?.EscapeMarkup()}");
                AnsiConsole.MarkupLine($"[dim]  {result.Message.EscapeMarkup()}[/]");
                return ExitCodes.Ok;

            case JiraWriteOutcome.PendingAuthentication:
                // result.Message is the recorded reason, not a fixed "Jira rejected the credentials"
                // claim: AuthorizeAsync classifies both a real 401 and a credential the vault could
                // not even resolve as PendingAuthentication (retriable either way), and only the
                // first of those is actually a claim Jira examined and rejected (independent pre-PR
                // review, adversarial lens, cycle 1).
                AnsiConsole.MarkupLine(
                    $"[yellow]{result.Message.EscapeMarkup()}[/] — this write is recorded and pending for task {shortId}.");
                AnsiConsole.MarkupLine(
                    "[dim]  If the registered API token was revoked or rotated, create a fresh one at "
                    + "https://id.atlassian.com/manage-profile/security/api-tokens and register the "
                    + "connection again with 'h9k connection add jira'; if the credential itself could "
                    + "not be resolved (an unexported environment variable, a missing file), fix that "
                    + "and the daemon retries this exact write automatically — nothing needs to be "
                    + "recomposed or resubmitted.[/]");
                return ExitCodes.Ok;

            default:
                throw new DomainValidationException(result.Message);
        }
    }
}
