using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A lesson's one terminal act (idea d805fd8b, piece 1; backlog 55): it stopped earning its
/// line, and here is why. Appends and never deletes. Nothing retires a lesson on age or on
/// absence of reinforcement — a lesson that works suppresses its own evidence, so ranking by
/// recent corroboration would retire exactly the lessons doing the most work.
/// </summary>
public sealed class LearningRetireCommand : Hall9kAsyncCommand<LearningRetireCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<LESSON>")]
        [Description("The lesson to retire: its id, or an unambiguous fragment of one")]
        public string Learning { get; init; } = string.Empty;

        [CommandOption("--reason <TEXT>")]
        [Description(
            "Why it stopped earning its line: wrong, absorbed into something that says it better, or "
            + "graduated into a rule now enforced somewhere harder — a gate, a repo skill, an AGENTS.md "
            + "entry. Required, because those three endings mean very different things to the next reader")]
        public string Reason { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        LearningRetired retired =
            await RunAsync(session, settings, context.OwnerId, DateTimeOffset.UtcNow, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[yellow]Lesson retired[/] [dim]({DomainId.Short(retired.Id)})[/] {retired.Reason.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"[dim]Nothing was deleted. It still reads back:[/] h9k learn show {DomainId.Short(retired.Id)} "
            + "[dim]· and lists under:[/] h9k learn list --all");
        return ExitCodes.Ok;
    }

    /// <summary>Everything but the store round trip and the printing, so the write is testable through the same seam the recording commands use.</summary>
    internal static async Task<LearningRetired> RunAsync(
        IDocumentSession session, Settings settings, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        LearningAggregate learning = await LearningIdResolver.LoadAsync(session, settings.Learning, cancellationToken);
        LearningRetired retired = LearningDecider.Retire(learning, settings.Reason, ownerId, now);
        session.Events.Append(learning.Id, retired);
        return retired;
    }
}
