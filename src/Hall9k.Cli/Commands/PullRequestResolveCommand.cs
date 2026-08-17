using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The on-demand PR-closeout trigger (Decisions Log #20): reopen a done task so the daemon
/// dispatches a follow-up run on the task's existing pull-request branch to resolve review
/// feedback. The automatic monitor (backlog 04) drives this same reopen path.
/// </summary>
public sealed class PullRequestResolveCommand : Hall9kAsyncCommand<PullRequestResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<TASK>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Task { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why the follow-up is needed (defaults to unresolved review comments)")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Task, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Guid previousRunId = task.CurrentRunId
            ?? throw new DomainConflictException($"Task {taskId} has no recorded run to follow up on.");
        RunDetails previousRun = await session.LoadAsync<RunDetails>(previousRunId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s run {previousRunId} has no run record.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        session.Events.Append(taskId, TaskDecider.Reopen(
            task, previousRunId, previousRun.Branch,
            settings.Reason ?? "Unresolved review comments on the pull request.",
            DateTimeOffset.UtcNow, context.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        await Doorbell.RingAsync($"pr-resolve:{taskId}", cancellationToken);

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} reopened — a follow-up run will resume branch {previousRun.Branch} for {task.PullRequestUrl}.[/]");
        return ExitCodes.Ok;
    }
}
