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
    /// One attention group, attention-first inside it (NeedsHuman, then stalled, then the
    /// rest), bounded — a pane that scrolls has stopped being glanceable.
    /// </summary>
    private static int Section(
        IReadOnlyList<TaskStatusRow> rows, AttentionBucket bucket, string stateWord, string heading, DateTimeOffset now)
    {
        List<TaskStatusRow> matching = [.. rows
            .Where(row => row.Attention == bucket)
            .OrderBy(row => row.Priority)
            .ThenByDescending(row => row.AddedAt)];
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
