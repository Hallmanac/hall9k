using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskShowCommand : Hall9kAsyncCommand<TaskShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous prefix)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Table header = new Table().Border(TableBorder.None).HideHeaders();
        header.AddColumns("k", "v");
        header.AddRow("[bold]Objective[/]", details.Objective.EscapeMarkup());
        header.AddRow("State", TaskListCommand.StateMarkup(details.State));
        header.AddRow("Type", details.Type.Value.EscapeMarkup());
        header.AddRow("Id", $"[dim]{details.Id}[/]");
        header.AddRow("Assigned to", await AssigneeMarkupAsync(session, details, cancellationToken));
        if (details.Model != AgentModel.Unknown)
        {
            header.AddRow("Model", $"{details.Model.Value.EscapeMarkup()} [dim](task override)[/]");
        }

        if (details.SourceIdeaId is { } sourceIdeaId)
        {
            // The other half of promotion's two-way provenance (Decisions Log #35): the idea's
            // stream names this task, and this names the idea it came from.
            header.AddRow("From idea",
                $"[dim]{TaskListCommand.ShortId(sourceIdeaId)}[/] "
                + $"[dim](h9k idea show {TaskListCommand.ShortId(sourceIdeaId)})[/]");
        }

        if (details.ExternalReference.IsNotBlank())
        {
            header.AddRow("External", details.ExternalReference.EscapeMarkup());
        }

        if (details.PullRequestUrl.IsNotBlank())
        {
            header.AddRow("PR", $"[link]{details.PullRequestUrl.EscapeMarkup()}[/]");
        }

        if (details.DependencyFailureReason.IsNotBlank())
        {
            header.AddRow("Dependency", $"[red]{details.DependencyFailureReason.EscapeMarkup()}[/]");
        }

        if (details.FailureReason.IsNotBlank())
        {
            header.AddRow("Failure", $"[red]{details.FailureReason.EscapeMarkup()}[/]");
        }

        if (details.RetryReason.IsNotBlank())
        {
            header.AddRow("Retried", $"[yellow]{details.RetryReason.EscapeMarkup()}[/]");
        }

        if (details.ResolvedReason.IsNotBlank())
        {
            header.AddRow("Resolved", $"[green]{details.ResolvedReason.EscapeMarkup()}[/]");
        }

        if (details.AbandonedReason.IsNotBlank())
        {
            header.AddRow("Abandoned", $"[dim]{details.AbandonedReason.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(header);

        AnsiConsole.MarkupLine("\n[bold]Acceptance criteria[/]");
        if (details.AcceptanceCriteria.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "  [dim]none yet — publishing requires at least one checkable criterion (PLAN.md §4)[/]");
        }

        foreach (string criterion in details.AcceptanceCriteria)
        {
            AnsiConsole.MarkupLine($"  • {criterion.EscapeMarkup()}");
        }

        if (details.BlockedBy.Count > 0)
        {
            IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(
                session, details.BlockedBy, cancellationToken);
            AnsiConsole.MarkupLine(
                "\n[bold]Blocked by[/] [dim](met only at true closeout: the pull request merged)[/]");
            foreach (TaskDependency dependency in dependencies)
            {
                AnsiConsole.MarkupLine(
                    $"  {DependencyMark(dependency)} [dim]{TaskListCommand.ShortId(dependency.Id)}[/] "
                    + $"{dependency.Objective.EscapeMarkup()} [dim]({dependency.State.Value})[/]");
            }
        }

        if (details.AgentContext.IsNotBlank())
        {
            AnsiConsole.MarkupLine("\n[bold]Agent context[/]");
            AnsiConsole.WriteLine(details.AgentContext);
        }

        if (details.Conversation.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Conversation[/]");
            foreach (TaskQuestion question in details.Conversation)
            {
                AnsiConsole.MarkupLine($"  [yellow]Q[/] {question.Question.EscapeMarkup()} [dim]({question.AskedAt:g})[/]");
                if (question.Answer.IsNotBlank())
                {
                    AnsiConsole.MarkupLine($"  [green]A[/] {question.Answer.EscapeMarkup()} [dim]({question.AnsweredAt:g})[/]");
                }
            }
        }

        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(r => r.TaskId == taskId)
            .OrderBy(r => r.DispatchedAt)
            .ToListAsync(cancellationToken);
        if (runs.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Runs[/]");
            Table runsTable = new Table().Border(TableBorder.Rounded);
            runsTable.AddColumns("Run", "Gen", "State", "Model", "Dispatched", "PR");
            foreach (RunListItem run in runs)
            {
                // A run dispatched before the model chain existed recorded none; "-" says
                // unknown rather than naming a model the run may never have used.
                runsTable.AddRow(
                    $"[dim]{TaskListCommand.ShortId(run.Id)}[/]",
                    run.LeaseGeneration.ToString(),
                    run.State.Value.EscapeMarkup(),
                    (run.Model == AgentModel.Unknown ? "-" : run.Model.Value).EscapeMarkup(),
                    run.DispatchedAt.ToLocalTime().ToString("g").EscapeMarkup(),
                    (run.PullRequestUrl ?? "-").EscapeMarkup());
            }

            AnsiConsole.Write(runsTable);
        }

        AnnounceNextStep(details);

        if (details.State == TaskState.Failed)
        {
            string shortId = TaskListCommand.ShortId(details.Id);
            AnsiConsole.MarkupLine("\n[bold]Failed is a waypoint, not an ending — three exits:[/]");
            AnsiConsole.MarkupLine($"  [yellow]retry[/]    h9k task retry {shortId} --reason <why>              — run it again");
            AnsiConsole.MarkupLine($"  [green]resolve[/]  h9k task resolve {shortId} --reason <why> [[--pr <url>]] — the objective was met despite the failure");
            AnsiConsole.MarkupLine($"  [dim]abandon[/]  h9k task abandon {shortId} [[--reason <why>]]          — walk away");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Three answers, not two: a blocker that will never close out reads differently from one
    /// that simply has not yet, because only one of them needs a human (Decisions Log #34).
    /// </summary>
    private static string DependencyMark(TaskDependency dependency) => dependency switch
    {
        { Blocks: false } => "[green]closed out[/]",
        { IsDead: true } => "[red]never closes out[/]",
        _ => "[yellow]waiting[/]",
    };

    /// <summary>
    /// Whose nodes may claim this task. Unassigned is a fact, not a gap: nothing dispatches
    /// until a human assigns it (Decisions Log #34).
    /// </summary>
    private static async Task<string> AssigneeMarkupAsync(
        IQuerySession session, TaskDetails details, CancellationToken cancellationToken)
    {
        if (details.AssignedOwnerId is not { } ownerId)
        {
            return "[dim]nobody — an unassigned task never dispatches[/]";
        }

        OwnerDetails? owner = await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken);
        return owner is null ? $"[dim]{ownerId}[/]" : owner.Name.EscapeMarkup();
    }

    /// <summary>
    /// The one next act for where the task actually is. The lifecycle has several explicit
    /// steps (Decisions Log #34), so every state says which one it is waiting for rather than
    /// leaving the reader to remember the graph.
    /// </summary>
    private static void AnnounceNextStep(TaskDetails details)
    {
        string shortId = TaskListCommand.ShortId(details.Id);
        string? next = details.State.Value switch
        {
            "Draft" => details.AcceptanceCriteria.Count == 0
                ? $"[dim]Next:[/] h9k task revise {shortId} --criteria \"…\" [dim]— publishing needs at least one[/]"
                : $"[dim]Next:[/] h9k task publish {shortId} [dim]then[/] h9k task assign {shortId}",
            "Published" => $"[dim]Next:[/] h9k task assign {shortId} [dim]— it will not run until you do[/]",
            "Blocked" => $"[dim]It queues itself when its dependencies close out. To stop waiting:[/] "
                + $"h9k task unassign {shortId} [dim]→[/] h9k task draft {shortId} [dim]→[/] h9k task revise {shortId} --clear-dependencies",
            "Queued" => $"[dim]Waiting for a dispatch cycle on one of the assignee's nodes. To take it back:[/] h9k task unassign {shortId}",
            _ => null,
        };

        if (next is not null)
        {
            AnsiConsole.MarkupLine($"\n{next}");
        }
    }
}
