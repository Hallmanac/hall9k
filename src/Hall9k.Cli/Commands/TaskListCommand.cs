using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

public sealed class TaskListCommand : Hall9kAsyncCommand<TaskListCommand.Settings>
{
    /// <summary>Rows shown when nothing else is asked for. Task volume grows; the browse surface should not.</summary>
    internal const int DefaultLimit = 20;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description("Only this project's tasks: its name, an unambiguous fragment of it, or its id (h9k project list shows them all)")]
        public string? Project { get; init; }

        [CommandOption("--state <STATE>")]
        [Description(
            "Only tasks in this state. Three vocabularies, and no word belongs to two of them. A "
            + "lifecycle state selects exactly what the Status column shows (Draft, Published, Working, "
            + "Delivered, Done, Failed, Archived); Delivered is pushed-but-not-merged and Done means the "
            + "merge was observed, the same bar the dependency rule uses (PLAN.md #66). An attention group "
            + "selects the whole group h9k status counts by (" + TaskStateFilter.AttentionSpelling + "); the "
            + "attention- spellings are the four groups the Status column names too, and the bare word there "
            + "is the column's, so --state delivered reaches every Delivered row including the one parked on "
            + "a question. A run state selects on the phase line's material (Running, Verifying, UnderReview, "
            + "AwaitingReview, ChecksFailing, …), which is where the run vocabulary reads now; a run that "
            + "failed is spelled run-failed, because Failed alone is the lifecycle state. Hyphens and case "
            + "are optional.")]
        public string? State { get; init; }

        [CommandOption("--limit <N>")]
        [Description("How many rows to show, newest first (default 20). The footer says how many were held back.")]
        public int? Limit { get; init; }

        [CommandOption("--all")]
        [Description("Show every matching task, unbounded — this wins over --limit")]
        public bool All { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Limit is { } requested && requested < 1)
        {
            throw new DomainValidationException($"--limit must be at least 1, got {requested}.");
        }

        if (settings.State.IsNotBlank())
        {
            TaskStateFilter.Validate(settings.State);
        }

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        // Resolve the project first: a misspelled name must say so even when the filtered
        // result would have been empty anyway.
        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : null;

        IReadOnlyList<TaskStatusRow> all = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        if (all.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No tasks. Draft one:[/] h9k task add --project <name> --objective \"…\"");
            return ExitCodes.Ok;
        }

        List<TaskStatusRow> matched = [.. all
            .Where(row => project is null || row.ProjectId == project.Id)
            .Where(row => settings.State.IsBlank() || TaskStateFilter.Matches(row, settings.State))
            .OrderByDescending(row => row.AddedAt)];
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No tasks match {Filters(settings, project)}. Drop a filter, or browse everything:[/] h9k task list --all");
            return ExitCodes.Ok;
        }

        int limit = settings.All ? matched.Count : settings.Limit ?? DefaultLimit;
        List<TaskStatusRow> shown = [.. matched.Take(limit)];

        AnsiConsole.Write(Rows(shown, scoped: project is not null, AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));
        AnsiConsole.MarkupLine(Footer(matched.Count, shown.Count, settings, project));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The browse list. The fixed-width columns never wrap, so a narrow terminal spends its
    /// width on the objective instead of stacking every id and state down the page; the
    /// objective is truncated to exactly the width those columns leave it. Built apart from
    /// the query so the layout can be rendered and measured without a database.
    /// <para>
    /// Every row carries its summary line underneath (<see cref="TaskStatusRow.SummaryMarkup"/>).
    /// That line is what keeps the lifecycle Status column from costing this surface the
    /// distinctions the run vocabulary used to carry here: an unassigned, a queued and a blocked
    /// task all read Published in the column, and say which they are on the line below. The
    /// attention column beside them answers the question a browse surface never could
    /// (Decisions Log #66) — whether this row wants a human.
    /// </para>
    /// </summary>
    internal static IRenderable Rows(
        IReadOnlyList<TaskStatusRow> rows, bool scoped, int consoleWidth, DateTimeOffset now)
    {
        string[] ages = [.. rows.Select(row => $"[dim]{row.AgeMarkup(now)}[/]")];
        // Deliberately no assignee column here. This list already carries seven fixed columns,
        // and an eighth pushes the objective onto its floor at the widths the one-line promise
        // is measured at (TaskTableLayoutTests) — while saying nothing a single-owner install
        // does not already know. The attention pane names the assignee, h9k task show names it
        // per task, and the browse list is where it goes when multi-owner projects arrive and
        // the column finally distinguishes something (Decisions Log #34, IDEA-task-assignment).
        return TaskRowLayout.Render(
            rows,
            [
                new TaskColumn("Id", [.. rows.Select(row => row.IdMarkup)]),
                new TaskColumn("Status", [.. rows.Select(row => row.StateMarkup)]),
                new TaskColumn("Type", [.. rows.Select(row => row.TypeMarkup)]),
                .. scoped
                    ? (TaskColumn[])[]
                    : [new TaskColumn("Project", [.. rows.Select(row => row.ProjectMarkup)])],
            ],
            [
                new TaskColumn("Attention", [.. rows.Select(row => row.AttentionMarkup)]),
                new TaskColumn("Added", ages),
                new TaskColumn("PR", [.. rows.Select(row => row.PullRequestMarkup)]),
            ],
            [.. rows.Select(row => row.SummaryMarkup)],
            consoleWidth,
            headers: true);
    }

    /// <summary>
    /// The bounded-list contract: never let a truncated view read as the whole truth. Says
    /// what was held back and the exact flag that shows it.
    /// </summary>
    internal static string Footer(int matched, int shown, Settings settings, ProjectDetails? project)
    {
        string scope = $"{shown} of {matched}{Scope(settings, project)}, newest first";
        int held = matched - shown;
        return held > 0
            ? $"[dim]{scope} · {held} held back — see them with:[/] h9k task list --all"
              + $"{Repeat(settings, project)} [dim]or[/] --limit {matched}"
            : $"[dim]{scope} · filter with:[/] h9k task list --project <name> --state <state>";
    }

    private static string Scope(Settings settings, ProjectDetails? project)
    {
        string scope = project is null ? string.Empty : $" in {project.Name.EscapeMarkup()}";
        return settings.State.IsNotBlank() ? $"{scope} matching --state {settings.State.EscapeMarkup()}" : scope;
    }

    /// <summary>The active filters, echoed so --all keeps the view the reader is looking at.</summary>
    private static string Repeat(Settings settings, ProjectDetails? project) =>
        (project is null ? string.Empty : $" --project {project.Name.EscapeMarkup()}")
        + (settings.State.IsBlank() ? string.Empty : $" --state {settings.State.EscapeMarkup()}");

    private static string Filters(Settings settings, ProjectDetails? project)
    {
        List<string> filters = [];
        if (project is not null)
        {
            filters.Add($"--project {project.Name.EscapeMarkup()}");
        }

        if (settings.State.IsNotBlank())
        {
            filters.Add($"--state {settings.State.EscapeMarkup()}");
        }

        return filters.Count > 0 ? string.Join(" ", filters) : "those filters";
    }

    // UUIDv7 front-loads the timestamp, so same-batch ids share their first chars;
    // the random tail is what tells them apart.
    internal static string ShortId(Guid id) => id.ToString("N")[^8..];

    /// <summary>
    /// A value cut to fit a column, on a text-element boundary rather than at a raw char index.
    /// Since adoption (PLAN.md §3.1a) the objectives passing through here are issue titles, and an
    /// emoji or any other character outside the BMP is two chars: slicing between its halves
    /// leaves a lone surrogate, which renders as the replacement character. The rule is
    /// <see cref="RelayedText"/>'s, so the daemon cuts relayed text the same way.
    /// </summary>
    internal static string Truncate(string value, int max) => RelayedText.Truncate(value, max);
}
