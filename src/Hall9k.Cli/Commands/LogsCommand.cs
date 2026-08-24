using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class LogsCommand : Hall9kAsyncCommand<LogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--run <RUN_ID>")]
        [Description("A specific run (default: the task's latest)")]
        public string? Run { get; init; }

        [CommandOption("--raw")]
        [Description("Dump the raw stream-json instead of the rendered transcript")]
        public bool Raw { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        IReadOnlyList<RunListItem> runs = await session.Query<RunListItem>()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.DispatchedAt)
            .ToListAsync(cancellationToken);

        if (runs.Count == 0)
        {
            throw new DomainNotFoundException($"Task {taskId} has no runs yet.");
        }

        RunListItem run = settings.Run.IsBlank()
            ? runs[0]
            : runs.FirstOrDefault(r => r.Id.ToString("N").EndsWith(settings.Run.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainNotFoundException($"No run matching '{settings.Run}' on task {taskId}.");

        string streamFile = RunPaths.StreamFile(run.RunDirectory);
        if (!File.Exists(streamFile))
        {
            throw new DomainNotFoundException(
                $"No stream file for run {run.Id} on this machine ({streamFile}). " +
                "It may have run on another node.");
        }

        AnsiConsole.MarkupLine(
            $"[dim]run {run.Id} · {run.State.Value} · dispatched {run.DispatchedAt.ToLocalTime():g}[/]\n");

        IEnumerable<string> lines = File.ReadLines(streamFile);
        if (settings.Raw)
        {
            foreach (string line in lines)
            {
                Console.WriteLine(line);
            }
        }
        else
        {
            foreach (string rendered in StreamRenderer.Render(lines))
            {
                AnsiConsole.MarkupLine(rendered);
            }
        }

        return ExitCodes.Ok;
    }
}
