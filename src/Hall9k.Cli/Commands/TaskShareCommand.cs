using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Shares a task with the team on the owner's own word (idea 8c5993c5) — sugar for
/// <c>h9k task scope &lt;id&gt; team</c>, and the one door onto team scope for a draft that is not
/// ready to publish yet (idea 18464daa's own use: sharing a draft for publish approval). Works on a
/// draft as well as a published task. Refused when the task is already team scope, since team is
/// one-way — a published task always is, so this is mostly useful before publish.
/// </summary>
public sealed class TaskShareCommand : Hall9kAsyncCommand<TaskShareCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskScopeSet set = TaskDecider.Share(task, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(taskId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine($"[green]Task {shortId} is now shared with the team.[/]");
        return ExitCodes.Ok;
    }
}
