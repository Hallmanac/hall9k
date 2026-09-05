using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
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
/// Flips a task's standing pre-approval after publish (task: a task can be published
/// pre-approved) — settable on any live non-terminal task, without the unassign/draft/revise/
/// publish ceremony a readiness-contract change would otherwise need.
/// </summary>
public sealed class TaskSetPreApprovedCommand : Hall9kAsyncCommand<TaskSetPreApprovedCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<true|false>")]
        [Description(
            "on/true to give standing pre-approval, off/false to withdraw it — the owner becomes a "
            + "synchronous gate at the pull request again the moment this lands")]
        public string Value { get; init; } = string.Empty;
    }

    private static bool ParseValue(string raw, Guid taskId) => raw.Trim().ToLowerInvariant() switch
    {
        "true" or "on" or "yes" => true,
        "false" or "off" or "no" => false,
        _ => throw new DomainValidationException(
            $"'{raw}' is not on/true or off/false (task {taskId}) — pass one of those to set or withdraw "
            + "this task's pre-approval."),
    };

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        bool preApproved = ParseValue(settings.Value, taskId);

        // The same "task is Done and its current run reached RunState.Completed" test
        // TaskDependencyQuery.IsClosedOut uses for true closeout — the aggregate alone cannot
        // answer it, since a merge observation lands on the run stream, never the task's.
        bool taskClosedOut = task.State == TaskState.Done
            && task.CurrentRunId is { } currentRunId
            && (await session.LoadAsync<RunDetails>(currentRunId, cancellationToken))?.State == RunState.Completed;

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskPreApprovedSet set = TaskDecider.SetPreApproved(
            task, preApproved, DateTimeOffset.UtcNow, context.OwnerId, taskClosedOut);
        session.Events.Append(taskId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(preApproved
            ? $"[green]Task {shortId} is now pre-approved[/] — the daemon merges its pull request on its own "
                + "once GitHub's own gates are satisfied; every human waypoint (Failed, a review park, a "
                + "cap trip) still stops it exactly as before."
            : $"[green]Task {shortId} is no longer pre-approved[/] — the owner is a synchronous gate at its "
                + "pull request again.");
        return ExitCodes.Ok;
    }
}
