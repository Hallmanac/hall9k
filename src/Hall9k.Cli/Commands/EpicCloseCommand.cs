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
/// closes (Decisions Log #100) — never automatically, not even when its last member
/// task closes out. A solution epic closes in every copy this node holds, still as the one act.
/// </summary>
public sealed class EpicCloseCommand : Hall9kAsyncCommand<EpicCloseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description(
            "Epic id (full, or an unambiguous fragment). A solution epic is named by its solution epic key or any "
            + "copy's id, and closing it closes every copy this node holds")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--reason <REASON>")]
        [Description("Why this epic is done. Required: closing without a reason is exactly the automatic close this platform never does")]
        public string? Reason { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        IReadOnlyList<EpicAggregate> copies = await EpicIdResolver.LoadAsync(session, settings.Id, cancellationToken);
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        IReadOnlyList<EpicClosed> closes = Close(copies, settings.Reason ?? string.Empty, DateTimeOffset.UtcNow, context.OwnerId);
        foreach (EpicClosed closed in closes)
        {
            session.Events.Append(closed.Id, closed);
        }

        await session.SaveChangesAsync(cancellationToken);

        string reference = EpicIdResolver.ReferenceOf(copies[0].Id, copies[0].SolutionEpicKey);
        AnsiConsole.MarkupLine(
            $"[dim]Epic {reference} closed{(closes.Count > 1 ? $" in {closes.Count} projects" : string.Empty)}:[/] {(settings.Reason ?? string.Empty).Trim().EscapeMarkup()}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The closes for every copy of the epic this node holds, as one act with one reason. A copy a replicated
    /// close already reached is left as it is, so closing the rest of a solution epic is not refused for the
    /// part that is done; when none is open the decider refuses as it does for a lone epic that is not open.
    /// </summary>
    internal static IReadOnlyList<EpicClosed> Close(
        IReadOnlyList<EpicAggregate> copies, string reason, DateTimeOffset closedAt, Guid closedByOwnerId)
    {
        EpicAggregate[] open = [.. copies.Where(copy => copy.State == EpicState.Open)];
        EpicAggregate[] toClose = open.Length > 0 ? open : [copies[0]];
        return [.. toClose.Select(epic => EpicDecider.Close(epic, reason, closedAt, closedByOwnerId))];
    }
}
