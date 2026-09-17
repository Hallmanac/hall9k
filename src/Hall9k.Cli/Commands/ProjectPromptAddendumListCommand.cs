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
/// Every prompt builder this project could address an addendum to, and whether it currently has
/// one (idea b9b09779, piece 6).
/// </summary>
public sealed class ProjectPromptAddendumListCommand : Hall9kAsyncCommand<ProjectPromptAddendumListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;
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

        Table table = new Table().AddColumns("Builder", "Length", "Set", "Over cap");
        foreach (PromptBuilderKey builder in PromptBuilderKey.All)
        {
            if (project.PromptAddenda.TryGetValue(builder.Value, out ProjectPromptAddendum? addendum))
            {
                table.AddRow(
                    builder.Value,
                    addendum.Content.Length.ToString(CultureInfo.InvariantCulture),
                    addendum.SetAt.ToString("u", CultureInfo.InvariantCulture),
                    addendum.OverCap ? "[yellow]yes[/]" : "no");
            }
            else
            {
                table.AddRow(builder.Value, "[dim]none[/]", "[dim]—[/]", "[dim]—[/]");
            }
        }

        AnsiConsole.Write(table);

        // The daemon's own PromptAddendaSweepEngine records this here the moment a ledger push
        // keeps failing — otherwise the only sign is a daemon log line nobody watching this CLI
        // would ever see, and every addendum above would go on reporting "set" forever with no
        // hint that none of them have actually reached the ledger (independent pre-PR review,
        // cycle 1, adversarial lens, medium).
        PromptAddendaSyncPosition? position = await session.LoadAsync<PromptAddendaSyncPosition>(project.Id, cancellationToken);
        if (position is { LastPushError: { } error })
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] the daemon has not been able to push this project's own prompt-addenda "
                + $"changes to the ledger since {position.LastPushErrorAt:u} — every addendum above is set here "
                + $"but may not have reached any prompt yet. Last error: {error.EscapeMarkup()}");
        }

        return ExitCodes.Ok;
    }
}
