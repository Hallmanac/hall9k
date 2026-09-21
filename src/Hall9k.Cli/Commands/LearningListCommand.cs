using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Browse recorded lessons (idea d805fd8b, piece 1; backlog 55). The same cheap indexed filter
/// over the projection row that <see cref="DecisionListCommand"/> runs, and the query backlog
/// 55's prompt injection will read once it lands: scope, scope id, status.
/// </summary>
public sealed class LearningListCommand : Hall9kAsyncCommand<LearningListCommand.Settings>
{
    /// <summary>Rows shown when nothing else is asked for; the footer says what was held back.</summary>
    internal const int DefaultLimit = 20;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Only this project's lessons: its name, an unambiguous fragment of it, or its id. With "
            + "neither --project nor --owner, every scope is listed and the scope column says which is which")]
        public string? Project { get; init; }

        [CommandOption("--owner")]
        [Description("Only your own cross-project lessons — the habits that hold wherever you work")]
        public bool Owner { get; init; }

        [CommandOption("--all")]
        [Description(
            "Include retired lessons. They are never deleted, only left out of the default view and "
            + "out of whatever gets injected into a prompt")]
        public bool All { get; init; }

        [CommandOption("--limit <N>")]
        [Description("How many rows to show, newest first (default 20)")]
        public int? Limit { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        // Read-only, and the owner is looked up rather than bootstrapped — see
        // DecisionListCommand's own identical note for why a list command must not call
        // NodeBootstrap.EnsureAsync.
        Guid ownerId = settings.Owner
            ? (await OwnerResolver.ResolveOrSoleAsync(session, null, cancellationToken)).Id
            : Guid.Empty;

        IReadOnlyList<LearningDetails> matched = await QueryAsync(session, settings, ownerId, cancellationToken);
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.All
                ? "[dim]No lessons recorded in that scope yet. Record one:[/] h9k learn \"…\""
                : "[dim]No active lessons in that scope. Retired ones are still there:[/] h9k learn list --all");
            return ExitCodes.Ok;
        }

        int limit = settings.Limit ?? DefaultLimit;
        IReadOnlyList<LearningDetails> shown = [.. matched.Take(limit)];
        Dictionary<Guid, string> projectNames = await KnowledgeRecordRendering.ProjectNamesAsync(
            session, shown.Select(row => (row.Scope, row.ScopeId)), cancellationToken);

        AnsiConsole.Write(Rows(shown, projectNames, settings.All, DateTimeOffset.UtcNow));
        AnsiConsole.MarkupLine(matched.Count > shown.Count
            ? $"[dim]{shown.Count} of {matched.Count} — raise it with --limit. One lesson in full:[/] "
              + "h9k learn show <id>"
            : "[dim]One lesson in full, with the run it came out of:[/] h9k learn show <id>");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The query itself, exposed for the tests that prove the filter rather than the table. See
    /// <see cref="DecisionListCommand.QueryAsync"/> for why the closed-vocabulary predicates go
    /// through <c>MatchesSql</c> rather than a value-object comparison.
    /// </summary>
    internal static async Task<IReadOnlyList<LearningDetails>> QueryAsync(
        IQuerySession session, Settings settings, Guid ownerId, CancellationToken cancellationToken)
    {
        if (settings.Limit is { } requested && requested < 1)
        {
            throw new DomainValidationException($"--limit must be at least 1, got {requested}.");
        }

        if (settings.Owner && settings.Project.IsNotBlank())
        {
            throw new DomainValidationException("--owner and --project ask for opposite scopes; pick one.");
        }

        IQueryable<LearningDetails> query = session.Query<LearningDetails>();

        if (settings.Owner)
        {
            string ownerScope = KnowledgeScope.Owner;
            query = query
                .Where(learning => learning.MatchesSql("d.data ->> 'scope' = ?", ownerScope))
                .Where(learning => learning.ScopeId == ownerId);
        }
        else if (settings.Project.IsNotBlank())
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
            string projectScope = KnowledgeScope.Project;
            query = query
                .Where(learning => learning.MatchesSql("d.data ->> 'scope' = ?", projectScope))
                .Where(learning => learning.ScopeId == project.Id);
        }

        if (!settings.All)
        {
            string active = LearningStatus.Active;
            query = query.Where(learning => learning.MatchesSql("d.data ->> 'status' = ?", active));
        }

        return await query.OrderByDescending(learning => learning.RecordedAt).ToListAsync(cancellationToken);
    }

    internal static Table Rows(
        IReadOnlyList<LearningDetails> learnings,
        IReadOnlyDictionary<Guid, string> projectNames,
        bool showStatus,
        DateTimeOffset now)
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty).Width(9).NoWrap());
        table.AddColumn(new TableColumn(string.Empty).Width(14).NoWrap());
        if (showStatus)
        {
            table.AddColumn(new TableColumn(string.Empty).Width(9).NoWrap());
        }

        table.AddColumn(new TableColumn(string.Empty).Width(8).NoWrap());
        table.AddColumn(new TableColumn(string.Empty));

        foreach (LearningDetails learning in learnings)
        {
            List<string> cells =
            [
                $"[dim]{DomainId.Short(learning.Id)}[/]",
                KnowledgeRecordRendering.ScopeCell(learning.Scope, learning.ScopeId, projectNames),
            ];
            if (showStatus)
            {
                cells.Add(learning.Status == LearningStatus.Retired ? "[yellow]retired[/]" : "[green]active[/]");
            }

            cells.Add($"[dim]{TaskStatusComposer.RelativeAge(now - learning.RecordedAt)}[/]");
            cells.Add(learning.Statement.EscapeMarkup());
            table.AddRow([.. cells]);
        }

        return table;
    }
}
