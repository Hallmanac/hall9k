using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.Exceptions;
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

        Guid taskId = await ResolveIdAsync(session, settings.Id, cancellationToken);
        TaskDetails details = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Table header = new Table().Border(TableBorder.None).HideHeaders();
        header.AddColumns("k", "v");
        header.AddRow("[bold]Objective[/]", details.Objective.EscapeMarkup());
        header.AddRow("State", TaskListCommand.StateMarkup(details.State));
        header.AddRow("Type", details.Type.Value.EscapeMarkup());
        header.AddRow("Id", $"[dim]{details.Id}[/]");
        if (details.ExternalReference.IsNotBlank())
        {
            header.AddRow("External", details.ExternalReference.EscapeMarkup());
        }

        if (details.PullRequestUrl.IsNotBlank())
        {
            header.AddRow("PR", $"[link]{details.PullRequestUrl.EscapeMarkup()}[/]");
        }

        if (details.FailureReason.IsNotBlank())
        {
            header.AddRow("Failure", $"[red]{details.FailureReason.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(header);

        AnsiConsole.MarkupLine("\n[bold]Acceptance criteria[/]");
        foreach (string criterion in details.AcceptanceCriteria)
        {
            AnsiConsole.MarkupLine($"  • {criterion.EscapeMarkup()}");
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
            runsTable.AddColumns("Run", "Gen", "State", "Dispatched", "PR");
            foreach (RunListItem run in runs)
            {
                runsTable.AddRow(
                    $"[dim]{run.Id.ToString()[..8]}[/]",
                    run.LeaseGeneration.ToString(),
                    run.State.Value.EscapeMarkup(),
                    run.DispatchedAt.ToLocalTime().ToString("g").EscapeMarkup(),
                    (run.PullRequestUrl ?? "-").EscapeMarkup());
            }

            AnsiConsole.Write(runsTable);
        }

        return ExitCodes.Ok;
    }

    private static async Task<Guid> ResolveIdAsync(IQuerySession session, string idOrPrefix, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(idOrPrefix, out Guid id))
        {
            return id;
        }

        IReadOnlyList<TaskListItem> all = await session.Query<TaskListItem>().ToListAsync(cancellationToken);
        Guid[] matches = [.. all
            .Where(t => t.Id.ToString().StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)];

        return matches switch
        {
            [Guid single] => single,
            [] => throw new DomainNotFoundException($"No task matches '{idOrPrefix}'."),
            _ => throw new DomainConflictException($"'{idOrPrefix}' is ambiguous ({matches.Length} matches) — use more characters."),
        };
    }
}
