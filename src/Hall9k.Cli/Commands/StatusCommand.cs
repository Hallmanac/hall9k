using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The attention pane, not a browse surface: what needs you, what has gone quiet, what is
/// moving. Everything else is a count in the header — browsing lives under the nouns
/// (h9k task list, h9k project list).
/// </summary>
public sealed class StatusCommand : Hall9kAsyncCommand<StatusCommand.Settings>
{
    /// <summary>Rows per section before the pane stops listing and points at the browse surface.</summary>
    private const int PerSection = 8;

    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // A quiet queue must never be a mystery (Decisions Log #31): say up front when
        // nothing is dispatching and what to do about it.
        if (DaemonProcess.Probe() is null)
        {
            AnsiConsole.MarkupLine(
                "[red]daemon not running[/] — tasks queue but do not dispatch; start it with [bold]h9k daemon start[/]");
        }

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing tracked yet. Draft some work with h9k task add.[/]");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine($"[bold]h9k status[/] · {TaskRollup.From(rows).Summary()}");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int listed = 0;
        listed += Section(rows, AttentionBucket.NeedsYou, "needs-you", "[red bold]Needs you[/]", now);
        listed += Section(rows, AttentionBucket.Stalled, "stalled",
            "[red]Stalled[/] [dim]— claimed and live, but the agent stream has been silent for over an hour[/]", now);
        listed += Section(rows, AttentionBucket.Active, "active", "[yellow]Running[/]", now);
        // The queue is normally a count in the header — it needs nothing from anyone. It earns
        // a section only when the node is at its concurrency ceiling (Decisions Log #64),
        // because that is the one case where a board with nothing dispatching is working
        // exactly as designed, and a human who cannot see that goes looking for the fault.
        if (rows.Any(row => row.WaitingForSlot))
        {
            // The lever is named once for the whole section rather than repeated on every row:
            // it is one setting for the node, not a per-task action. The environment form is
            // the one that reaches an installed daemon, whose working directory is never its
            // binary directory, so the published appsettings.json is not read there
            // (DaemonLogging carries that observation); options bind at startup, hence the
            // restart.
            listed += Section(rows, AttentionBucket.Queued, "queued",
                "[blue]Queued[/] [dim]— the node is at its concurrency ceiling; each of these starts as a "
                + "run finishes. Raise[/] Hall9k__MaxConcurrentAgentSessions [dim]and restart the daemon to run "
                + "more at once — it is counted in agent sessions, and a run under review holds one per review "
                + "lens[/]", now, inServiceOrder: true);
        }

        // Blocked work is neither running nor waiting on you, but the wait has a cause worth
        // seeing: each row names the dependencies it is still waiting to close out (log #34).
        listed += Section(rows, AttentionBucket.Blocked, "blocked",
            "[cyan]Blocked[/] [dim]— assigned, waiting on dependencies to close out[/]", now);

        if (listed == 0)
        {
            AnsiConsole.MarkupLine("\n[green]Nothing needs you and nothing is running.[/]");
            if (TaskRollup.From(rows) is { Draft: > 0 } or { Ready: > 0 })
            {
                AnsiConsole.MarkupLine(
                    "[dim]Drafts and published tasks are counted above; neither dispatches until you "
                    + "publish and assign:[/] h9k task list --state draft [dim]·[/] h9k task list --state ready");
            }
        }

        AnsiConsole.MarkupLine(
            "\n[dim]Browse it all:[/] h9k task list [dim](--project <name>, --state <state>) · per project:[/] h9k project list");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// One attention group, bounded — a pane that scrolls has stopped being glanceable.
    /// </summary>
    private static int Section(
        IReadOnlyList<TaskStatusRow> rows, AttentionBucket bucket, string stateWord, string heading,
        DateTimeOffset now, bool inServiceOrder = false)
    {
        IReadOnlyList<TaskStatusRow> matching = SectionRows(rows, bucket, inServiceOrder);
        if (matching.Count == 0)
        {
            return 0;
        }

        AnsiConsole.MarkupLine($"\n{heading}");
        AnsiConsole.Write(SectionTable([.. matching.Take(PerSection)], AnsiConsole.Profile.Width, now));

        int held = matching.Count - Math.Min(matching.Count, PerSection);
        if (held > 0)
        {
            AnsiConsole.MarkupLine($"[dim]  … and {held} more — see them with:[/] h9k task list --state {stateWord}");
        }

        return matching.Count;
    }

    /// <summary>
    /// One section's rows in the order it lists them: attention-first (NeedsHuman, then stalled,
    /// then the rest, newest first inside each), or — for the deferred queue — the order the
    /// dispatcher will actually serve them in.
    /// </summary>
    /// <param name="inServiceOrder">
    /// Oldest assignment first, ties broken by when the task was added, which is exactly the
    /// claim query's ordering (Decisions Log #64). The queue section tells a human that each of
    /// its rows starts as a run finishes, so its top row has to be the one that starts next;
    /// listed newest-first, the pane's default everywhere else, it showed the eight tasks that
    /// run last and collapsed the imminent ones into "… and N more" (pre-PR review, 2026-08-22).
    /// A row with nothing assigned cannot be in that section — the dispatcher cannot see an
    /// unassigned task — but it sorts last rather than first if one ever is.
    /// </param>
    internal static IReadOnlyList<TaskStatusRow> SectionRows(
        IReadOnlyList<TaskStatusRow> rows, AttentionBucket bucket, bool inServiceOrder)
    {
        IEnumerable<TaskStatusRow> inBucket = rows.Where(row => row.Attention == bucket);
        return [.. inServiceOrder
            ? inBucket
                .OrderBy(row => row.AssignedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(row => row.AddedAt)
            : inBucket
                .OrderBy(row => row.Priority)
                .ThenByDescending(row => row.AddedAt)];
    }

    /// <summary>
    /// One section's rows, borderless because the pane is a list rather than a report. The
    /// objective is truncated to exactly the width the other columns leave it, so a glanceable
    /// pane stays one line per task. Built apart from the query so the layout can be measured.
    /// </summary>
    internal static Table SectionTable(IReadOnlyList<TaskStatusRow> rows, int consoleWidth, DateTimeOffset now)
    {
        string[] activity = [.. rows.Select(row => row.Activity.IsNotBlank()
            ? $"[dim]{row.Activity}[/]"
            : $"[dim]added {row.AgeMarkup(now)}[/]")];
        // The owner column earns its width only where there is an assignee to show; on a
        // one-owner install it would otherwise repeat one name down the pane (log #34).
        bool assigned = rows.Any(row => row.Assignee.IsNotBlank());
        int objective = TaskStatusRow.ObjectiveWidth(consoleWidth, bordered: false,
        [
            ["id", .. rows.Select(row => row.IdMarkup)],
            ["status", .. rows.Select(row => row.StatusMarkup)],
            ["project", .. rows.Select(row => row.ProjectMarkup)],
            .. assigned ? (string[][])[["owner", .. rows.Select(row => row.AssigneeMarkup)]] : [],
            ["activity", .. activity],
            ["pr", .. rows.Select(row => row.PullRequestMarkup)],
        ]);

        // The headers are hidden but still sized for, so they are measured above with the cells.
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns([
            "id", "status", "project", .. assigned ? (string[])["owner"] : [], "objective", "activity", "pr",
        ]);

        for (int index = 0; index < rows.Count; index++)
        {
            TaskStatusRow row = rows[index];
            table.AddRow([
                row.IdMarkup,
                row.StatusMarkup,
                row.ProjectMarkup,
                .. assigned ? (string[])[row.AssigneeMarkup] : [],
                row.ObjectiveMarkup(objective),
                activity[index],
                row.PullRequestMarkup,
            ]);
        }

        return table;
    }
}
