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
/// Answer a plan's park: the step that parked runs again, and a draft that parked on rounds exhausted is granted one more
/// round. Publishing or abandoning a parked draft by hand answers its park too, and the plan carries on with no resume.
/// </summary>
public sealed class PlanResumeCommand : Hall9kAsyncCommand<PlanResumeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLAN>")]
        [Description("Plan id (full, or an unambiguous fragment). It must be live and parked")]
        public string Plan { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        PlanDetails found = await PlanIdResolver.ResolveAsync(session, settings.Plan, cancellationToken);
        StreamState fence = await session.Events.FetchStreamStateAsync(found.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No plan {found.Id}.");
        PlanAggregate plan = await session.Events.AggregateStreamAsync<PlanAggregate>(found.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No plan {found.Id}.");
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        PlanResumed resumed = PlanDecider.Resume(plan, context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(plan.Id, expectedVersion: fence.Version + 1, resumed);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Plan {plan.Id} changed while this was being recorded, so nothing was resumed. Check h9k plan show {TaskListCommand.ShortId(plan.Id)} and resume again if it is still parked.");
        }

        await Doorbell.RingAsync($"plan-resumed:{plan.Id}", cancellationToken);
        AnsiConsole.MarkupLine($"[blue]Plan {TaskListCommand.ShortId(plan.Id)} resumed[/]");
        foreach (PlanDraftState draft in plan.Drafts.Where(draft => draft.IsParked))
        {
            string granted = draft.ParkReason == PlanParkReason.RoundsExhausted
                ? ", and granted one more round"
                : string.Empty;
            AnsiConsole.MarkupLine(
                $"  {TaskListCommand.ShortId(draft.DraftId)}: the step that parked ({draft.ParkReason.Description}) runs again{granted}");
        }

        AnsiConsole.MarkupLine($"[dim]  This returns without waiting. Read where it stands with:[/] h9k plan show {TaskListCommand.ShortId(plan.Id)}");
        return ExitCodes.Ok;
    }
}
