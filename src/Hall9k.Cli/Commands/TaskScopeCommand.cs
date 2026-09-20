using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Sets a task's own replication scope (idea 8c5993c5): private (this node only), fleet (every node
/// this owner runs), or team (every project member's own fleet). Settable on any task, in any
/// state, a draft included — refused only when the task is already at that scope, or already team
/// and asked to go narrower, since team is one-way (publishing already sets team unconditionally).
/// </summary>
public sealed class TaskScopeCommand : Hall9kAsyncCommand<TaskScopeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<private|fleet|team>")]
        [Description(
            "private: never leaves this node. fleet: reaches every node this same owner runs. "
            + "team: reaches every project member's own fleet, one-way once set (publishing sets it too).")]
        public string Scope { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        ReplicationScope scope = ScopeInput.Parse(settings.Scope);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskScopeSet set = TaskDecider.SetScope(task, scope, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(taskId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine($"[green]Task {shortId} is now {scope.Value.ToLowerInvariant()} scope.[/]");
        return ExitCodes.Ok;
    }
}
