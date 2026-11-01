using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Why a plan on this node is not starting sessions or requesting challenges, read the way the daemon decides it: the
/// project's max parallel tasks of 0, this node's launch hold, or this node's spent spend budget. The daemon's own check is
/// <c>PlanEngine</c>'s, and this reads the same three facts, so <c>h9k plan show</c> and the daemon cannot disagree about
/// why a plan is waiting. Null when none of the three holds.
/// </summary>
internal static class PlanLaunchGate
{
    public static async Task<string?> ReadAsync(
        IQuerySession session, ProjectDetails project, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (project.MaxParallelTasks == 0)
        {
            return $"project '{project.Name}' has max parallel tasks 0 (h9k project set {project.Name} --max-parallel-tasks to raise it)";
        }

        try
        {
            if (await LaunchHoldStatus.ReadAsync(session, cancellationToken) is { Active: true } hold)
            {
                return $"this node's launch hold stands since {hold.RaisedAt:u}: {ExternalText.OneLine(hold.CauseText)}";
            }

            OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cancellationToken);
            SpendPressure spend = await SpendPressure.ReadAsync(session, report, now, cancellationToken);
            return spend.AtBudget
                ? spend.ReasonLine
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
