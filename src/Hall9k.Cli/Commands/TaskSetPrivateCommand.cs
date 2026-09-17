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
/// Sets or clears a task's private flag (idea 202383dc, M2a): while private, this task's own
/// stream never rides an outbox to a teammate's node, whatever its <c>EventScope</c> classification
/// says — a draft a human wants to keep to themselves a while longer stays that way until this
/// command clears it. Settable on any task, in any state.
/// </summary>
public sealed class TaskSetPrivateCommand : Hall9kAsyncCommand<TaskSetPrivateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandArgument(1, "<on|off>")]
        [Description(
            "on/true to keep this task's own events off every outbox until cleared; off/false to let "
            + "it replicate again on the daemon's next sweep")]
        public string Value { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        bool isPrivate = ParseBool(settings.Value);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        TaskPrivacySet set = TaskDecider.SetPrivate(task, isPrivate, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(taskId, set);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(taskId);
        AnsiConsole.MarkupLine(isPrivate
            ? $"[green]Task {shortId} is now private[/] — its events stay off every outbox until cleared."
            : $"[green]Task {shortId} is no longer private[/] — it replicates on the daemon's next sweep.");
        return ExitCodes.Ok;
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" => true,
        "off" or "false" => false,
        _ => throw new DomainValidationException($"'{value}' is not on/true or off/false."),
    };
}
