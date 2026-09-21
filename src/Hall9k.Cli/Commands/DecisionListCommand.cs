using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Decision;
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
/// Browse recorded decisions (idea d805fd8b, piece 1). The filter is a cheap indexed query over
/// the projection row, never a stream replay: <see cref="DecisionDetails"/> carries the scope
/// coordinate, the status and the recorded-at that this reads, and the scope id is the one
/// indexed column (<c>MartenConfiguration.ConfigureHall9k</c>).
/// </summary>
public sealed class DecisionListCommand : Hall9kAsyncCommand<DecisionListCommand.Settings>
{
    /// <summary>Rows shown when nothing else is asked for; the footer says what was held back.</summary>
    internal const int DefaultLimit = 20;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Only this project's decisions: its name, an unambiguous fragment of it, or its id. With "
            + "neither --project nor --owner, every scope is listed and the scope column says which is which")]
        public string? Project { get; init; }

        [CommandOption("--owner")]
        [Description("Only your own cross-project decisions — the habits that hold wherever you work")]
        public bool Owner { get; init; }

        [CommandOption("--all")]
        [Description(
            "Include superseded decisions. They are never deleted, only left out of the default view, "
            + "because what binds today is the ordinary question")]
        public bool All { get; init; }

        [CommandOption("--limit <N>")]
        [Description("How many rows to show, newest first (default 20)")]
        public int? Limit { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        // Read-only, and the owner is looked up rather than bootstrapped: NodeBootstrap.EnsureAsync
        // registers this machine's node and owner, and on an install with no connection on file it
        // shells out to gh to do it — neither of which a list command has any business doing. Only
        // --owner needs an owner at all, so nothing is resolved otherwise.
        Guid ownerId = settings.Owner
            ? (await OwnerResolver.ResolveOrSoleAsync(session, null, cancellationToken)).Id
            : Guid.Empty;

        IReadOnlyList<DecisionDetails> matched = await QueryAsync(session, settings, ownerId, cancellationToken);
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.All
                ? "[dim]No decisions recorded in that scope yet. Record one:[/] h9k decide \"…\""
                : "[dim]No decisions still binding in that scope. Superseded ones are still there:[/] "
                  + "h9k decide list --all");
            return ExitCodes.Ok;
        }

        int limit = settings.Limit ?? DefaultLimit;
        IReadOnlyList<DecisionDetails> shown = [.. matched.Take(limit)];
        Dictionary<Guid, string> projectNames = await KnowledgeRecordRendering.ProjectNamesAsync(
            session, shown.Select(row => (row.Scope, row.ScopeId)), cancellationToken);

        AnsiConsole.Write(Rows(shown, projectNames, settings.All, DateTimeOffset.UtcNow));
        AnsiConsole.MarkupLine(matched.Count > shown.Count
            ? $"[dim]{shown.Count} of {matched.Count} — raise it with --limit. One decision in full:[/] "
              + "h9k decide show <id>"
            : "[dim]One decision in full, with its provenance and what it replaced:[/] h9k decide show <id>");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The query itself, exposed for the tests that prove the filter rather than the table. The
    /// scope and status predicates go through <c>MatchesSql</c> against the document's own jsonb
    /// field, the form <c>DispatchEngine</c>'s own queued-task query already uses for a closed
    /// vocabulary: the value object serializes as a bare string, and Marten's Linq provider has
    /// no translation for the record type itself (TASK-MODEL.md §8's query discipline).
    /// </summary>
    internal static async Task<IReadOnlyList<DecisionDetails>> QueryAsync(
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

        IQueryable<DecisionDetails> query = session.Query<DecisionDetails>();

        if (settings.Owner)
        {
            string ownerScope = KnowledgeScope.Owner;
            query = query
                .Where(decision => decision.MatchesSql("d.data ->> 'scope' = ?", ownerScope))
                .Where(decision => decision.ScopeId == ownerId);
        }
        else if (settings.Project.IsNotBlank())
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
            string projectScope = KnowledgeScope.Project;
            query = query
                .Where(decision => decision.MatchesSql("d.data ->> 'scope' = ?", projectScope))
                .Where(decision => decision.ScopeId == project.Id);
        }

        if (!settings.All)
        {
            string recorded = DecisionStatus.Recorded;
            query = query.Where(decision => decision.MatchesSql("d.data ->> 'status' = ?", recorded));
        }

        return await query.OrderByDescending(decision => decision.RecordedAt).ToListAsync(cancellationToken);
    }

    internal static Table Rows(
        IReadOnlyList<DecisionDetails> decisions,
        IReadOnlyDictionary<Guid, string> projectNames,
        bool showStatus,
        DateTimeOffset now)
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders().Expand();
        table.AddColumn(new TableColumn(string.Empty).Width(9).NoWrap());
        table.AddColumn(new TableColumn(string.Empty).Width(14).NoWrap());
        if (showStatus)
        {
            table.AddColumn(new TableColumn(string.Empty).Width(11).NoWrap());
        }

        table.AddColumn(new TableColumn(string.Empty).Width(8).NoWrap());
        table.AddColumn(new TableColumn(string.Empty));

        foreach (DecisionDetails decision in decisions)
        {
            List<string> cells =
            [
                $"[dim]{DomainId.Short(decision.Id)}[/]",
                KnowledgeRecordRendering.ScopeCell(decision.Scope, decision.ScopeId, projectNames),
            ];
            if (showStatus)
            {
                cells.Add(decision.Status == DecisionStatus.Superseded
                    ? "[yellow]superseded[/]"
                    : "[green]binding[/]");
            }

            cells.Add($"[dim]{TaskStatusComposer.RelativeAge(now - decision.RecordedAt)}[/]");
            cells.Add(decision.Statement.EscapeMarkup());
            table.AddRow([.. cells]);
        }

        return table;
    }
}
