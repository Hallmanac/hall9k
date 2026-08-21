using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class IdeaListCommand : Hall9kAsyncCommand<IdeaListCommand.Settings>
{
    /// <summary>Rows shown when nothing else is asked for; the footer says what was held back.</summary>
    internal const int DefaultLimit = 20;

    /// <summary>The states a human may ask for, spelled as they are typed.</summary>
    internal const string StateSpelling = "captured, promoted, discarded, all";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description(
            "Only this project's ideas: its name, an unambiguous fragment of it, or its id. "
            + "Ideas with no project are their own view — see them with --unassigned")]
        public string? Project { get; init; }

        [CommandOption("--unassigned")]
        [Description("Only ideas that have no project yet — the ones still deciding where they belong")]
        public bool Unassigned { get; init; }

        [CommandOption("--state <STATE>")]
        [Description(
            "Which ideas to show: " + StateSpelling + ". Defaults to captured — the ones still in "
            + "discovery — because a promoted idea's story continues on its task and a discarded "
            + "one is history. The footer always says how many were left out")]
        public string? State { get; init; }

        [CommandOption("--limit <N>")]
        [Description("How many rows to show, newest first (default 20)")]
        public int? Limit { get; init; }

        [CommandOption("--all")]
        [Description("Show every matching idea, unbounded — this wins over --limit")]
        public bool All { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Limit is { } requested && requested < 1)
        {
            throw new DomainValidationException($"--limit must be at least 1, got {requested}.");
        }

        IdeaState? state = ParseState(settings.State);
        if (settings.Project.IsNotBlank() && settings.Unassigned)
        {
            throw new DomainValidationException(
                "--project and --unassigned ask for opposite things; pick one.");
        }

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        // Resolve the project first: a misspelled name must say so even when the filtered
        // result would have been empty anyway.
        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : null;

        IReadOnlyList<IdeaDetails> ideas = await session.Query<IdeaDetails>().ToListAsync(cancellationToken);
        if (ideas.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No ideas yet. Capture one — the text is all it takes:[/] h9k idea add \"…\"");
            return ExitCodes.Ok;
        }

        Dictionary<Guid, ProjectDetails> projects = (await session.Query<ProjectDetails>()
            .ToListAsync(cancellationToken)).ToDictionary(p => p.Id);

        // The scope the reader asked for, before the state filter: the footer speaks for what
        // the state filter hides, so it has to count within this and not across every idea.
        List<IdeaDetails> scoped = [.. ideas
            .Where(idea => project is null || idea.ProjectId == project.Id)
            .Where(idea => !settings.Unassigned || idea.ProjectId is null)];
        List<IdeaRow> matched = [.. scoped
            .Where(idea => state is null || idea.State == state)
            .Select(idea => IdeaRow.Compose(idea, projects))
            .OrderByDescending(row => row.CapturedAt)];
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(EmptyMatch(settings, project, state, ideas.Count));
            return ExitCodes.Ok;
        }

        int limit = settings.All ? matched.Count : settings.Limit ?? DefaultLimit;
        List<IdeaRow> shown = [.. matched.Take(limit)];

        AnsiConsole.Write(Rows(shown, showState: state is null, scoped: project is not null,
            AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));
        AnsiConsole.MarkupLine(Footer(
            matched.Count, shown.Count, [.. scoped.Select(idea => idea.State)], settings, project, state));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The browse table. Fixed-width columns never wrap, so a narrow terminal spends its width
    /// on the note itself; the note is truncated to exactly what those columns leave it.
    /// </summary>
    internal static Table Rows(
        IReadOnlyList<IdeaRow> rows, bool showState, bool scoped, int consoleWidth, DateTimeOffset now)
    {
        string[] ages = [.. rows.Select(row => $"[dim]{row.AgeMarkup(now)}[/]")];
        int text = TaskStatusRow.ObjectiveWidth(consoleWidth, bordered: true,
        [
            ["Id", .. rows.Select(row => row.IdMarkup)],
            .. showState ? (string[][])[["State", .. rows.Select(row => row.StateMarkup)]] : [],
            .. scoped ? (string[][])[] : [["Project", .. rows.Select(row => row.ProjectMarkup)]],
            ["Captured", .. ages],
        ]);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Id").NoWrap());
        if (showState)
        {
            table.AddColumn(new TableColumn("State").NoWrap());
        }

        if (!scoped)
        {
            table.AddColumn(new TableColumn("Project").NoWrap());
        }

        table.AddColumn("Idea");
        table.AddColumn(new TableColumn("Captured").NoWrap());

        for (int index = 0; index < rows.Count; index++)
        {
            IdeaRow row = rows[index];
            table.AddRow([
                row.IdMarkup,
                .. showState ? (string[])[row.StateMarkup] : [],
                .. scoped ? (string[])[] : [row.ProjectMarkup],
                row.TextMarkup(text),
                ages[index],
            ]);
        }

        return table;
    }

    /// <summary>
    /// Two jobs: never let a truncated view read as the whole truth, and teach the one act the
    /// list exists to lead to — promotion, when discovery has given an idea intent.
    /// </summary>
    internal static string Footer(
        int matched, int shown, IReadOnlyList<IdeaState> scopedStates, Settings settings,
        ProjectDetails? project, IdeaState? state)
    {
        string scope = $"{shown} of {matched}{Scope(settings, project, state)}, newest first";
        int held = matched - shown;
        string counts = held > 0
            ? $"[dim]{scope} · {held} held back — see them with:[/] "
              + $"h9k idea list --all{Repeat(settings, project)}{RepeatState(settings, state)}"
            : $"[dim]{scope}{Elsewhere(scopedStates, settings, project, state)}[/]";

        return counts + "\n[dim]An idea with intent is a task:[/] h9k idea promote <id> "
            + "[dim](discovery ends, refinement begins)[/]";
    }

    /// <summary>
    /// What the state filter is hiding, counted <i>and named by the state it is actually in</i>.
    /// Only the state filter is spoken for here: --project and --unassigned are scopes the reader
    /// typed and can read back in the header, so what falls outside them was never being hidden.
    /// </summary>
    private static string Elsewhere(
        IReadOnlyList<IdeaState> scopedStates, Settings settings, ProjectDetails? project, IdeaState? state)
    {
        if (state is null)
        {
            return string.Empty;
        }

        string[] named = [.. scopedStates
            .Where(scoped => scoped != state)
            .GroupBy(scoped => scoped)
            .OrderBy(group => Vocabulary(group.Key))
            .Select(group => $"{group.Count()} {Named(group.Key)}")];

        return named.Length == 0
            ? string.Empty
            : $" · {string.Join(", ", named)} — see them with: h9k idea list --state all"
              + Repeat(settings, project);
    }

    /// <summary>The discovery vocabulary's own order, so the footer reads the same way every time.</summary>
    private static int Vocabulary(IdeaState state) => state switch
    {
        _ when state == IdeaState.Captured => 0,
        _ when state == IdeaState.Promoted => 1,
        _ when state == IdeaState.Discarded => 2,
        _ => 3,
    };

    /// <summary>An idea whose state was never recorded is labelled as that, never as a guess.</summary>
    private static string Named(IdeaState state) =>
        state == IdeaState.Unknown ? "in no recorded state" : state.Value.ToLowerInvariant();

    private static string Scope(Settings settings, ProjectDetails? project, IdeaState? state)
    {
        string scope = project is null ? string.Empty : $" in {project.Name.EscapeMarkup()}";
        scope += settings.Unassigned ? " with no project" : string.Empty;
        return state is null ? $"{scope} in every state" : $"{scope} {state.Value.ToLowerInvariant()}";
    }

    /// <summary>
    /// The scope filters, echoed so a suggested re-run keeps the view the reader is looking at.
    /// The state filter is deliberately not one of them: the two suggestions disagree about it,
    /// so each names its own (see <see cref="RepeatState"/>).
    /// </summary>
    private static string Repeat(Settings settings, ProjectDetails? project) =>
        (project is null ? string.Empty : $" --project {project.Name.EscapeMarkup()}")
        + (settings.Unassigned ? " --unassigned" : string.Empty);

    /// <summary>
    /// The state the reader is actually looking at, echoed so widening the bound does not
    /// quietly narrow the state: --all alone falls back to the default filter, which would show
    /// a different set of ideas than the view being expanded. A state the reader never typed is
    /// left unspoken, and "every state" is spelled the way the option spells it.
    /// </summary>
    private static string RepeatState(Settings settings, IdeaState? state) =>
        state is null
            ? " --state all"
            : settings.State.IsBlank()
                ? string.Empty
                : $" --state {state.Value.ToLowerInvariant()}";

    /// <summary>
    /// Why nothing showed. The default state filter is not one the reader typed, so an empty
    /// discovery list says what it is actually looking at rather than blaming "those filters".
    /// </summary>
    private static string EmptyMatch(Settings settings, ProjectDetails? project, IdeaState? state, int total)
    {
        bool defaulted = settings.State.IsBlank() && project is null && !settings.Unassigned;
        return defaulted
            ? $"[dim]Nothing is in discovery right now — all {total} idea(s) were promoted or discarded. "
              + "See them with:[/] h9k idea list --state all [dim]· capture a new one:[/] h9k idea add \"…\""
            : $"[dim]No ideas match {Filters(settings, project)}. Drop a filter, or see everything:[/] "
              + "h9k idea list --state all --all";
    }

    private static string Filters(Settings settings, ProjectDetails? project)
    {
        List<string> filters = [];
        if (project is not null)
        {
            filters.Add($"--project {project.Name.EscapeMarkup()}");
        }

        if (settings.Unassigned)
        {
            filters.Add("--unassigned");
        }

        if (settings.State.IsNotBlank())
        {
            filters.Add($"--state {settings.State.EscapeMarkup()}");
        }

        return filters.Count > 0 ? string.Join(" ", filters) : "those filters";
    }

    /// <summary>
    /// Null means "every state" (what --state all asks for); anything unrecognized is refused
    /// with the vocabulary quoted rather than silently matching nothing.
    /// </summary>
    internal static IdeaState? ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "captured" or "open" => IdeaState.Captured,
        "promoted" => IdeaState.Promoted,
        "discarded" => IdeaState.Discarded,
        "all" => null,
        _ => throw new DomainValidationException(
            $"Unknown idea state '{value}'. Use one of: {StateSpelling}."),
    };
}
