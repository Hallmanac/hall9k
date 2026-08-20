using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Published -> Draft: the explicit revert that reopens a task for revision (Decisions Log
/// #34). The ceremony is the point — a Published task promises a human may assign it at any
/// moment, so leaving that promise is something you say out loud.
/// </summary>
public sealed class TaskDraftCommand : Hall9kAsyncCommand<TaskDraftCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description(
            "Why the task is going back for refinement; recorded on TaskReturnedToDraft and left "
            + "unknown when omitted, never inferred")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(taskId, TaskDecider.ReturnToDraft(
            task, settings.Reason, DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine($"[blue]Task {shortId} returned to Draft[/] — editable again.");
        AnsiConsole.MarkupLine(
            $"[dim]Next:[/] h9k task revise {shortId} --objective/--criteria/--context/--blocked-by "
            + $"[dim]then[/] h9k task publish {shortId}");
        return ExitCodes.Ok;
    }
}
