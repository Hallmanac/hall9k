using System.ComponentModel;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One plan: its state and, for each draft, its phase, round, latest outcome, and where its latest report is; the reason and
/// the way forward for a park; and, while the plan is live, whether the daemon is running and why no session or challenge is
/// starting. The record holds no text a session wrote, so this prints where to read the rest.
/// </summary>
public sealed class PlanShowCommand : Hall9kAsyncCommand<PlanShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PLAN>")]
        [Description("Plan id (full, or an unambiguous fragment). Plans never leave the node they were started on, so only this node's are found")]
        public string Plan { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        PlanDetails plan = await PlanIdResolver.ResolveAsync(session, settings.Plan, cancellationToken);
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken);
        IReadOnlyList<TaskDetails> drafts = await session.LoadManyAsync<TaskDetails>(
            cancellationToken, [.. plan.Drafts.Select(draft => draft.DraftId)]);
        string? gate = plan.IsLive && project is not null
            ? await PlanLaunchGate.ReadAsync(session, project, DateTimeOffset.UtcNow, cancellationToken)
            : null;

        foreach (string line in PlanShowComposer.Compose(
            plan, project, drafts.ToDictionary(task => task.Id),
            new PlanShowFacts(DaemonProcess.Probe() is not null, gate), PlanRoundCap.Default))
        {
            AnsiConsole.MarkupLine(line);
        }

        return ExitCodes.Ok;
    }
}
