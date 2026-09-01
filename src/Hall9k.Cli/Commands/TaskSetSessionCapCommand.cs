using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Sets this task's own override of how many agent sessions its run may hold simultaneously
/// (Decisions Log #108, Brian's ruling 2026-08-30). Deliberately state-agnostic, unlike
/// <see cref="TaskReviseCommand"/>: it is meant to apply at any time, including while the task's
/// run is live, and takes effect at the run's very next session dispatch — raising it lets the
/// next phase fan out wider, and lowering it never terminates a session already running.
/// </summary>
public sealed class TaskSetSessionCapCommand : Hall9kAsyncCommand<TaskSetSessionCapCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<CAP>")]
        [Description(
            "How many agent sessions this task's run may hold simultaneously, at least 1. A cap of 1 serializes "
            + "the two review lenses instead of dispatching them together, for maximum throttle; today's routine "
            + "peak is 2, so anything above 2 is inert headroom until a future coded activity actually overlaps a "
            + "third session. Settable any time, including on a task with no live run yet — it takes effect at "
            + "the run's next session dispatch, once one exists, and never terminates a session already running. "
            + "An interactive claim (h9k task work) occupies zero runs and ignores this cap entirely.")]
        public int Cap { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskSessionCapOverridden overridden = TaskDecider.OverrideSessionCap(
            task, settings.Cap, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(taskId, overridden);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Task {TaskListCommand.ShortId(taskId)} session cap set to {settings.Cap}[/] — takes effect "
            + "at the run's next session dispatch, once one exists; a session already running is never terminated. "
            + "An interactive claim (h9k task work) occupies zero runs and ignores this cap.");
        return ExitCodes.Ok;
    }
}
