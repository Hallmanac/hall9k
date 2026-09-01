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
/// Overrides one or more of a task's own review-cycle caps (task: the review cycle caps become
/// settable at three levels) — deliberately state-agnostic, unlike h9k task revise: it is meant
/// to be set at any time, including while the task's run is live, so the daemon picks it up at
/// the next cap check. This is the documented takeover path for a task observed grinding: set a
/// cap at or below the cycles that track has run since its last human takeover grant (0, if it has
/// never had one) and the run parks at the next settle point or cap check, no new state or command
/// beyond this one — 0 always parks immediately, since that count can never be negative.
/// </summary>
public sealed class TaskSetReviewCapsCommand : Hall9kAsyncCommand<TaskSetReviewCapsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--max-compliance-review-cycles <N|default>")]
        [Description(
            "This task's cycle cap for the conformance review track (Decisions Log #63): outranks the "
            + "project's and the node's own settings. Setting it at or below the cycles that track has "
            + "run since its last human takeover grant (0, if it has never had one — not the same as the "
            + "absolute review cycle h9k status/h9k task show print, which never resets) parks the run at "
            + "the next settle point or cap check — the takeover lever for a task observed grinding; 0 "
            + "always parks immediately, since that count can never be negative. 'default' clears the task "
            + "override so the project (or the node) decides.")]
        public string? MaxComplianceReviewCycles { get; init; }

        [CommandOption("--max-adversarial-review-cycles <N|default>")]
        [Description(
            "This task's cycle cap for the adversarial review track (Decisions Log #63). Same resolution "
            + "order, takeover behavior, and clearing idiom as --max-compliance-review-cycles.")]
        public string? MaxAdversarialReviewCycles { get; init; }

        [CommandOption("--max-final-full-pass-rounds <N|default>")]
        [Description(
            "This task's cap on consecutive mandatory final-full-pass rounds (Decisions Log #93). Same "
            + "resolution order, takeover behavior, and clearing idiom as --max-compliance-review-cycles.")]
        public string? MaxFinalFullPassRounds { get; init; }

        [CommandOption("--lifetime-review-cycle-budget <N|default>")]
        [Description(
            "This task's own task-lifetime review-cycle budget — cycles counted across every run and "
            + "follow-up this task has had, immune to the per-run resets a stranding, retry, or follow-up "
            + "round otherwise gives the three caps above. Same resolution order and clearing idiom as "
            + "--max-compliance-review-cycles.")]
        public string? LifetimeReviewCycleBudget { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Optional<int?> maxComplianceReviewCycles = ClearableCapOption.Parse(
            settings.MaxComplianceReviewCycles, "--max-compliance-review-cycles");
        Optional<int?> maxAdversarialReviewCycles = ClearableCapOption.Parse(
            settings.MaxAdversarialReviewCycles, "--max-adversarial-review-cycles");
        Optional<int?> maxFinalFullPassRounds = ClearableCapOption.Parse(
            settings.MaxFinalFullPassRounds, "--max-final-full-pass-rounds");
        Optional<int?> lifetimeReviewCycleBudget = ClearableCapOption.Parse(
            settings.LifetimeReviewCycleBudget, "--lifetime-review-cycle-budget");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskReviewCapsOverridden overridden = TaskDecider.OverrideReviewCaps(
            task, maxComplianceReviewCycles, maxAdversarialReviewCycles, maxFinalFullPassRounds,
            lifetimeReviewCycleBudget, DateTimeOffset.UtcNow, context.OwnerId);

        session.Events.Append(taskId, overridden);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(
            $"[blue]Task {shortId} review caps updated[/]: {string.Join(", ", Changed(overridden))}.");
        AnsiConsole.MarkupLine(
            $"[dim]Takes effect at the next cap check — including on a run already live.[/] "
            + $"h9k task show {shortId}");
        return ExitCodes.Ok;
    }

    private static IEnumerable<string> Changed(TaskReviewCapsOverridden overridden)
    {
        if (overridden.MaxComplianceReviewCycles.HasValue)
        {
            yield return overridden.MaxComplianceReviewCycles.Value is { } value
                ? $"max-compliance-review-cycles = {value}"
                : "max-compliance-review-cycles cleared";
        }

        if (overridden.MaxAdversarialReviewCycles.HasValue)
        {
            yield return overridden.MaxAdversarialReviewCycles.Value is { } value
                ? $"max-adversarial-review-cycles = {value}"
                : "max-adversarial-review-cycles cleared";
        }

        if (overridden.MaxFinalFullPassRounds.HasValue)
        {
            yield return overridden.MaxFinalFullPassRounds.Value is { } value
                ? $"max-final-full-pass-rounds = {value}"
                : "max-final-full-pass-rounds cleared";
        }

        if (overridden.LifetimeReviewCycleBudget.HasValue)
        {
            yield return overridden.LifetimeReviewCycleBudget.Value is { } value
                ? $"lifetime-review-cycle-budget = {value}"
                : "lifetime-review-cycle-budget cleared";
        }
    }
}
