using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The attention pane, not a browse surface: what needs you, what has gone quiet, what is
/// moving. Everything else is a count in the header — browsing lives under the nouns
/// (h9k task list, h9k project list).
/// <para>
/// Every row is up to two lines (Decisions Log #66). The first carries the three surfaces side
/// by side: the lifecycle STATE, the objective, and the ATTENTION column's yes-or-no. The second
/// carries the words — the PHASE line for live work, the derived facts for a Published row, and
/// the attention cause with its lever. A settled row has no second line at all, so a quiet board
/// stays one line per task.
/// </para>
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

        // Observability precedes enforcement (backlog: spend-governor step three): shown whether
        // or not a budget is set, so the calibration loop (run, observe a week's real burn, set
        // the budget under it, adjust) starts the day this merges rather than the day a number is
        // chosen. Degraded rather than fatal on a DB hiccup — the daemon-not-running banner above
        // already covers the "nothing is reachable" case, and this pane's whole job is to still
        // say something useful when part of the picture is missing.
        SpendPressure? spend = null;
        try
        {
            OperatingSettingsReport spendReport = await OperatingSettingsResolver.ResolveAsync(cancellationToken);
            spend = await SpendPressure.ReadAsync(session, spendReport, now, cancellationToken);
            AnsiConsole.MarkupLineInterpolated($"[dim]{spend.SummaryLine}[/]");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]spend this period: unavailable ({exception.Message})[/]");
        }
        int listed = 0;
        listed += Section(rows, AttentionBucket.NeedsYou, "needs-you", "[red bold]Needs you[/]", now);
        listed += Section(rows, AttentionBucket.Stalled, "stalled",
            "[red]Stalled[/] [dim]— live work whose stream has gone silent, or whose process is gone[/]", now);
        listed += Section(rows, AttentionBucket.Working, "attention-working",
            "[yellow]Working[/] [dim]— a run owns it and has not pushed yet[/]", now);
        // Delivered work is where "is it my turn?" gets answered, so the pane lists it rather
        // than counting it in the header: the phase line says whether a follow-up is driving the
        // pull request or the merge is the only thing left (origin incident, 2026-08-22, PR 24).
        listed += Section(rows, AttentionBucket.Delivered, "attention-delivered",
            "[magenta]Delivered[/] [dim]— pushed; the merge has not been observed[/]", now);
        // The queue is normally a count in the header — it needs nothing from anyone. It earns a
        // section when the node is at its concurrency ceiling (Decisions Log #64) or its spend
        // budget for the period is spent (backlog: spend-governor step three), because those are
        // the cases where a board with nothing dispatching is working exactly as designed, and a
        // human who cannot see that goes looking for the fault.
        bool atCeiling = rows.Any(row => row.WaitingForSlot);
        bool atSpendBudget = spend is { AtBudget: true };
        if (atCeiling || atSpendBudget)
        {
            // The lever is named once for the whole section rather than repeated on every row:
            // it is one setting for the node, not a per-task action. h9k config set writes the
            // daemon's durable operating settings (~/.hall9k/config.json), read by absolute path
            // regardless of the daemon's working directory; options bind at startup, hence the
            // restart. Run-denominated end to end (Decisions Log #111) — no operator-facing
            // surface needs to know how many sessions a run tree is worth to interpret this.
            string heading = (atCeiling, atSpendBudget) switch
            {
                (true, true) => "[blue]Queued[/] [dim]— the node is at its concurrency ceiling and its spend "
                    + "budget for this period is spent; each of these starts once both clear. Run[/] "
                    + "h9k config set --max-concurrent-task-runs <n> [dim]or[/] --spend-budget <n> "
                    + $"[dim]as the cause warrants, then restart the daemon ({spend!.ReasonLine})[/]",
                (false, true) => $"[blue]Queued[/] [dim]— this node's {spend!.ReasonLine}. Run[/] "
                    + "h9k config set --spend-budget <n> [dim]and restart the daemon to raise it, or wait for the "
                    + "period to roll[/]",
                _ => "[blue]Queued[/] [dim]— the node is at its concurrency ceiling; each of these starts as a "
                    + "run finishes. Run[/] h9k config set --max-concurrent-task-runs <n> [dim]and restart the "
                    + "daemon to run more at once[/]",
            };
            listed += Section(rows, AttentionBucket.Queued, "queued", heading, now, inServiceOrder: true);
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
            "\n[dim]Browse it all:[/] h9k task list --include-archived "
            + "[dim](--project <name>, --state <state>) · per project:[/] h9k project list");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// One attention group, attention-first inside it (the rows asking for something, then the
    /// rest, newest first), bounded — a pane that scrolls has stopped being glanceable.
    /// </summary>
    /// <param name="stateWord">
    /// How --state spells this group, for the hint under a bounded section. It has to be the
    /// group's own spelling rather than the lifecycle word beside it in the heading: a bare
    /// Status-column word selects the column now (Brian's ruling, 2026-08-22), which would send
    /// a reader who was promised "N more of these" to a different set of rows.
    /// </param>
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
        AnsiConsole.Write(SectionRows([.. matching.Take(PerSection)], AnsiConsole.Profile.Width, now));

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
        IEnumerable<TaskStatusRow> inGroup = rows.Where(row => row.Group == bucket);
        return [.. inServiceOrder
            ? inGroup
                .OrderBy(row => row.AssignedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(row => row.AddedAt)
            : inGroup
                .OrderBy(row => row.Priority)
                .ThenByDescending(row => row.Attention.Level)
                .ThenByDescending(row => row.AddedAt)];
    }

    /// <summary>
    /// One section's rows, in the layout every task surface shares (<see cref="TaskRowLayout"/>):
    /// the fixed columns, the objective, and every one of the row's detail lines underneath at
    /// full width. The pane prints all of them — it is a handful of rows by design, and the
    /// phase, the facts and the ask are the whole reason it exists.
    /// <para>
    /// Headerless on purpose: the pane is read by its section headings, and the width a header
    /// row would cost goes to the objective instead.
    /// </para>
    /// </summary>
    internal static IRenderable SectionRows(
        IReadOnlyList<TaskStatusRow> rows, int consoleWidth, DateTimeOffset now)
    {
        string[] activity = [.. rows.Select(row => row.Activity.IsNotBlank()
            ? $"[dim]{row.Activity}[/]"
            : $"[dim]added {row.AgeMarkup(now)}[/]")];
        // The owner column earns its width only where there is an assignee to show; on a
        // one-owner install it would otherwise repeat one name down the pane (log #34).
        bool assigned = rows.Any(row => row.Assignee.IsNotBlank());

        // The objective sits after the assignee and before the ask, where the eye reaches it
        // once it has the state and before it is told whether the row wants something.
        return TaskRowLayout.Render(
            rows,
            [
                new TaskColumn("Id", [.. rows.Select(row => row.IdMarkup)]),
                new TaskColumn("Status", [.. rows.Select(row => row.StateMarkup)]),
                new TaskColumn("Project", [.. rows.Select(row => row.ProjectMarkup)]),
                .. assigned
                    ? (TaskColumn[])[new TaskColumn("Owner", [.. rows.Select(row => row.AssigneeMarkup)])]
                    : [],
            ],
            [
                new TaskColumn("Attention", [.. rows.Select(row => row.AttentionMarkup)]),
                new TaskColumn("Activity", activity),
                new TaskColumn("PR", [.. rows.Select(row => row.PullRequestMarkup)]),
            ],
            [.. rows.Select(row => row.DetailMarkup)],
            consoleWidth,
            headers: false);
    }
}
