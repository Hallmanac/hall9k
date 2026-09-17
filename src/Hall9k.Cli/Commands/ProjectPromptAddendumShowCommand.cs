using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Prints one builder's own addendum exactly as this node's own event stream last recorded it
/// (idea b9b09779, piece 6) — this node's own audit trail, never a live ledger read: see
/// <c>ProjectPromptAddendumSetCommand</c>'s own doc for why the ledger itself is daemon-only.
/// </summary>
public sealed class ProjectPromptAddendumShowCommand : Hall9kAsyncCommand<ProjectPromptAddendumShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<BUILDER>")]
        [Description("Which shipped prompt builder to show the addendum for: work, review-lap, agent, or mention-follow-up.")]
        public string Builder { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static async Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        PromptBuilderKey builder = PromptBuilderKey.Parse(settings.Builder);

        if (!project.PromptAddenda.TryGetValue(builder.Value, out ProjectPromptAddendum? addendum))
        {
            AnsiConsole.MarkupLine($"[dim]'{project.Name.EscapeMarkup()}' has no {builder.Value} addendum.[/]");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine(
            $"[bold]{builder.Value}[/] addendum for '{project.Name.EscapeMarkup()}'"
            + (addendum.OverCap ? " [yellow](set over this project's usual length cap)[/]" : string.Empty) + ":");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(addendum.Content);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[dim]Set {addendum.SetAt.ToString("u", CultureInfo.InvariantCulture)} by owner {addendum.SetByOwnerId}"
            + (addendum.OverCap ? $" — over cap: {addendum.OverCapReason?.EscapeMarkup()}" : string.Empty) + ".[/]");

        // Mirrors ProjectPromptAddendumListCommand's own warning: this node's own audit trail
        // above is accurate, but a standing ledger-push failure means the daemon has not actually
        // gotten this content into the ledger yet, so no prompt anywhere carries it (independent
        // pre-PR review, cycle 1, adversarial lens, medium).
        PromptAddendaSyncPosition? position = await session.LoadAsync<PromptAddendaSyncPosition>(project.Id, cancellationToken);
        if (position is { LastPushError: { } error })
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] the daemon has not been able to push this project's own prompt-addenda "
                + $"changes to the ledger since {position.LastPushErrorAt:u} — this addendum may not have "
                + $"reached any prompt yet. Last error: {error.EscapeMarkup()}");
        }

        return ExitCodes.Ok;
    }
}
