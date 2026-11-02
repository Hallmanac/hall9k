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
/// Answer discovery's gate by not pursuing the idea. A normal ending: the plan is Stopped, which is neither a failure nor a
/// cancellation, and the idea stays Captured with its workspace, its report, and any tasks already cut from it untouched.
/// Accepted only at the gate.
/// </summary>
public sealed class PlanStopCommand : Hall9kAsyncCommand<PlanStopCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLAN>")]
        [Description("Plan id (full, or an unambiguous fragment). It must be waiting at discovery's gate (h9k status lists it under Needs you)")]
        public string Plan { get; init; } = string.Empty;

        [CommandOption("--reason <WHY>")]
        [Description("Why the idea is not being pursued, in a sentence; recorded on the plan. A blank reason is refused")]
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

        PlanStopped stopped = PlanDecider.Stop(plan, settings.Reason ?? string.Empty, context.OwnerId, DateTimeOffset.UtcNow);
        session.Events.Append(plan.Id, expectedVersion: fence.Version + 1, stopped);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Plan {plan.Id} changed while this was being recorded, so nothing was stopped. Check h9k plan show {TaskListCommand.ShortId(plan.Id)} and stop again if it is still at the gate.");
        }

        await Doorbell.RingAsync($"plan-stopped:{plan.Id}", cancellationToken);
        string idea = TaskListCommand.ShortId(stopped.IdeaId);
        AnsiConsole.MarkupLine($"[blue]Plan {TaskListCommand.ShortId(plan.Id)} stopped[/] at discovery's gate, a normal ending");
        AnsiConsole.MarkupLine(
            $"[dim]  Idea {idea} is still Captured, and its workspace, the report in it, and any task already cut from it are untouched. "
            + "Nothing was cut and no session runs.[/]");
        AnsiConsole.MarkupLine($"  h9k idea conclude {idea} --reason \"…\"   [dim]: something came of it[/]");
        AnsiConsole.MarkupLine($"  h9k idea archive {idea} --reason \"…\"    [dim]: nothing did[/]");
        AnsiConsole.MarkupLine($"  h9k plan start --idea {idea}            [dim]: another look is a new plan, with a report of its own beside this one[/]");
        return ExitCodes.Ok;
    }
}
