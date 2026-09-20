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
/// draft as well as a published task: a published task is always already team scope
/// (<see cref="Hall9k.Domain.Features.Tasks.TaskAggregate.Apply(Hall9k.Domain.Features.Tasks.Events.TaskPublished)"/>),
/// and sharing one again is a no-op success rather than a refusal.
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
        TaskScopeSet? set = TaskDecider.Share(task, DateTimeOffset.UtcNow, context.OwnerId);
        if (set is not null)
        {
            session.Events.Append(taskId, set);
        }

        // Saved unconditionally, not just on the widening branch: NodeBootstrap.EnsureAsync may
        // have staged a genesis OwnerRegistered/NodeRegistered/ConnectionRegistered on this same
        // session two lines above, and it never saves them itself — every other caller commits
        // (independent pre-PR review, idea 19489eff, cycle 11, adversarial, low). Skipping the save
        // on the idempotent no-op path silently dropped that staged write.
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine($"[green]Task {shortId} is now shared with the team.[/]");
        return ExitCodes.Ok;
    }
}
