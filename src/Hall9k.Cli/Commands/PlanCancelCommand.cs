using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// End a live plan. The daemon stops the plan's refinement session without applying anything, and the drafts stay Drafts,
/// free for another plan. Nothing already published is unpublished.
/// </summary>
public sealed class PlanCancelCommand : Hall9kAsyncCommand<PlanCancelCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLAN>")]
        [Description("Plan id (full, or an unambiguous fragment). It must be live")]
        public string Plan { get; init; } = string.Empty;

        [CommandOption("--reason <WHY>")]
        [Description("Why the plan is being cancelled, in a sentence; recorded on the plan. A blank reason is refused")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using DocumentStore store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        PlanDetails found = await PlanIdResolver.ResolveAsync(session, settings.Plan, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(found.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No plan {found.Id}.");
        PlanAggregate plan = await session.Events.AggregateStreamAsync<PlanAggregate>(found.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No plan {found.Id}.");
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        PlanCancelled cancelled = PlanDecider.Cancel(plan, settings.Reason ?? string.Empty, context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(plan.Id, expectedVersion: fence.Version + 1, cancelled);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Plan {plan.Id} changed while this was being recorded, so nothing was cancelled. Check h9k plan show {TaskListCommand.ShortId(plan.Id)} and cancel again if it is still live.");
        }

        await Doorbell.RingAsync($"plan-cancelled:{plan.Id}", cancellationToken);
        AnsiConsole.MarkupLine($"[blue]Plan {TaskListCommand.ShortId(plan.Id)} cancelled[/]");
        AnsiConsole.MarkupLine(
            "[dim]  The daemon stops the plan's refinement session without applying anything. Its drafts are still Drafts and free for "
            + "another plan; any already published stay published.[/]");
        return ExitCodes.Ok;
    }
}
