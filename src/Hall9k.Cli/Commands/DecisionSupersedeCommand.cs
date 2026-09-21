using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A decision's one terminal act (idea d805fd8b, piece 1): it stopped binding, and here is why.
/// Appends and never deletes — the statement, its origin incident and its whole provenance stay
/// queryable forever, and only its standing changes. That is the property the markdown log never
/// had: an edited file remembers the current rule and forgets the one it replaced.
/// </summary>
public sealed class DecisionSupersedeCommand : Hall9kAsyncCommand<DecisionSupersedeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<DECISION>")]
        [Description("The decision that stopped binding: its id, or an unambiguous fragment of one")]
        public string Decision { get; init; } = string.Empty;

        [CommandOption("--reason <TEXT>")]
        [Description(
            "Why it stopped binding: what changed, or what turned out to be wrong with it. Required, "
            + "because the next reader's question about a superseded decision is never what it said, it "
            + "is why it stopped being true")]
        public string Reason { get; init; } = string.Empty;

        [CommandOption("--by <DECISION>")]
        [Description(
            "The decision that replaced this one, when there is one already recorded. Leave it off for "
            + "a decision that was simply overruled and nothing took its place — the record then says "
            + "so rather than pointing at the nearest plausible successor. Recording the replacement "
            + "with h9k decide --supersedes <this one> does both halves in one act instead")]
        public string? By { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        DecisionSuperseded superseded =
            await RunAsync(session, settings, context.OwnerId, DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[yellow]Decision superseded[/] [dim]({DomainId.Short(superseded.Id)})[/] "
            + superseded.Reason.EscapeMarkup());
        AnsiConsole.MarkupLine(superseded.SupersededByDecisionId is { } by
            ? $"[dim]  replaced by:[/] {DomainId.Short(by)}"
            : "[dim]  replaced by: nothing — overruled, with no successor recorded[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Nothing was deleted. Read it back:[/] h9k decide show {DomainId.Short(superseded.Id)}");
        return ExitCodes.Ok;
    }

    /// <summary>Everything but the store round trip and the printing, so the write is testable through the same seam the recording commands use.</summary>
    internal static async Task<DecisionSuperseded> RunAsync(
        IDocumentSession session, Settings settings, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DecisionAggregate decision = await DecisionIdResolver.LoadAsync(session, settings.Decision, cancellationToken);

        // Loaded, not merely resolved (independent pre-PR review, cycle 1, both lenses): a full
        // GUID parses without anything having to exist behind it, so a mistyped --by recorded a
        // successor pointer at nothing, and h9k decide show then rendered it as "not held on this
        // node" — the honest label for a real decision this node has not replicated yet, not for
        // a typo. h9k decide --supersedes already loads for exactly this reason.
        Guid? by = settings.By.IsNotBlank()
            ? (await DecisionIdResolver.LoadAsync(session, settings.By, cancellationToken)).Id
            : null;

        DecisionSuperseded superseded = DecisionDecider.Supersede(decision, by, settings.Reason, ownerId, now);
        session.Events.Append(decision.Id, superseded);
        return superseded;
    }
}
