using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Closing an epic honestly: an explicit human act with a reason, and the only way one ever
/// closes (Brian's ruling, 2026-08-28) — never automatically, not even when its last member
/// task closes out.
/// </summary>
public sealed class EpicCloseCommand : Hall9kAsyncCommand<EpicCloseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Epic id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why this epic is done. Required: closing without a reason is exactly the automatic close this platform never does")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        EpicAggregate epic = await EpicIdResolver.LoadAsync(session, settings.Id, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        EpicClosed closed = EpicDecider.Close(
            epic, settings.Reason ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(epic.Id, closed);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(epic.Id);
        AnsiConsole.MarkupLine($"[dim]Epic {shortId} closed:[/] {closed.Reason.EscapeMarkup()}");
        return ExitCodes.Ok;
    }
}
