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
        // nothing is dispatching and what to do about it. A daemon that h9k daemon start
        // just spawned and that is still booting (assembly resolution and JIT for the
        // entry point, before it ever reaches its own single-instance guard, has taken up
        // to ~15s on at least one real machine, task 92da629d) is not "not running" — that
        // reading is exactly what invited a second h9k daemon start into the first spawn's
        // own singleton lock.
        switch (DaemonProcess.ProbeBootStatus().State)
        {
            case DaemonBootState.Running:
                break;
            case DaemonBootState.Starting:
                AnsiConsole.MarkupLine(
                    "[yellow]daemon starting[/] — a marker recorded moments ago says a spawn is in "
                    + "flight, though whether it is still booting isn't known here; tasks queue and "
                    + "will dispatch once it is up. Check h9k daemon status shortly.");
                break;
            default:
                AnsiConsole.MarkupLine(
                    "[red]daemon not running[/] — tasks queue but do not dispatch; start it with [bold]h9k daemon start[/]");
                break;
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
            foreach (string line in spend.ByModelLines)
            {
                AnsiConsole.MarkupLineInterpolated($"[dim]{line}[/]");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]spend this period: unavailable ({exception.Message})[/]");
        }

        // A project paused at a cap of 0 while the machine sits idle is the one queue state that
        // looks exactly like a fault (Decisions Log #140): the deliberate pause and the forgotten
        // one are the same setting, so the pane says out loud what is held, how much of it, and
        // the one lever that releases it. Nothing raises a cap on its own — the footgun is
        // answered with visibility, not automation — which is precisely why this line has to be
        // unmissable rather than tucked under a section heading.
        //
        // Read from this machine's own last sweep, not re-derived: a cap set a moment ago with no
        // sweep since is not yet a cap the dispatcher enforced, and claiming otherwise would have
        // the pane and the dispatcher disagree.
        DispatchPressure? pressure = null;
        try
        {
            pressure = await DispatchPressure.ReadAsync(session, now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]node capacity: unavailable ({exception.Message})[/]");
        }

        foreach (string line in PausedProjectLines(rows, pressure))
        {
            AnsiConsole.MarkupLine(line);
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
        // section when the node is at its concurrency ceiling (Decisions Log #64), when a project
        // is at its own cap or paused (#140), or when this node's spend budget for the period is
        // spent (backlog: spend-governor step three), because those are the cases where a board
        // with nothing dispatching is working exactly as designed, and a human who cannot see
        // that goes looking for the fault.
        bool atCeiling = rows.Any(row => row.Held?.Kind == QueueHoldKind.NodeCeiling);
        bool atProjectCap = rows.Any(row => row.Held is
            { Kind: QueueHoldKind.ProjectCap or QueueHoldKind.ProjectPaused });
        bool atSpendBudget = spend is { AtBudget: true };
        if (atCeiling || atProjectCap || atSpendBudget)
        {
            listed += Section(
                rows, AttentionBucket.Queued, "queued",
                QueuedHeading(atCeiling, atProjectCap, atSpendBudget, spend), now, inServiceOrder: true);
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
    /// The queued section's heading: every limit currently holding rows back, and the lever for
    /// each. Named once for the whole section rather than repeated on every row, because each is
    /// a setting rather than a per-task action — while each row's own second line still names
    /// which of the two <em>counted</em> limits holds that row, in that limit's numbers, so a
    /// mixed queue is never ambiguous (Decisions Log #64, #111, #140).
    /// <para>
    /// The spend budget is the exception, and the heading says so rather than promising a line
    /// that is not there: it is the node's own single figure with no per-task denominator, so
    /// <see cref="QueueHold"/> knows nothing of it and a row it holds names no limit of its own.
    /// The unconditional promise sent a reader hunting a cause that was never rendered, on the
    /// one surface whose whole job is that a queue state never has to be reconstructed
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// <para>
    /// The node's two levers write the daemon's durable operating settings
    /// (<c>~/.hall9k/config.json</c>, read by absolute path regardless of the daemon's working
    /// directory) and bind at startup, hence the restart. A project's cap is read from the
    /// project's own stream on every sweep, so it needs none — which is worth saying here, since
    /// the two sit side by side in the same heading.
    /// </para>
    /// </summary>
    internal static string QueuedHeading(
        bool atCeiling, bool atProjectCap, bool atSpendBudget, SpendPressure? spend)
    {
        List<string> causes = [];
        List<string> levers = [];
        if (atCeiling)
        {
            causes.Add("the node is at its concurrency ceiling");
            levers.Add("h9k config set --max-concurrent-task-runs <n> [dim](restart the daemon after)[/]");
        }

        if (atProjectCap)
        {
            causes.Add("a project is at its own cap, or paused at 0");
            levers.Add("h9k project set <project> --max-parallel-tasks <n> [dim](next dispatch cycle, no restart)[/]");
        }

        if (atSpendBudget)
        {
            causes.Add($"this node's {spend!.ReasonLine}");
            levers.Add("h9k config set --spend-budget <n> [dim](restart the daemon after, or wait for the period to roll)[/]");
        }

        // What the rows themselves can be relied on to say, which depends on whether the spend
        // budget is one of the causes: the two counted limits render their own numbers on the
        // held row, and the budget renders nothing there at all.
        string rowNote = (atSpendBudget, atCeiling || atProjectCap) switch
        {
            (true, true) => "Each row below held by one of the two counted limits names it, in that limit's own "
                + "numbers; a row naming none is waiting on the budget, which is this node's alone and has no "
                + "per-task number.",
            (true, false) => "No row below names a limit of its own: the budget is this node's alone and holds "
                + "the whole queue.",
            _ => "Each row below names which limit holds it, in that limit's own numbers.",
        };

        return $"[blue]Queued[/] [dim]— {string.Join("; ", causes)}. {rowNote} "
            + $"Raise one with:[/] {string.Join(" [dim]·[/] ", levers)}";
    }

    /// <summary>
    /// The unmissable line a project paused at a cap of 0 earns while this node sits idle
    /// (Decisions Log #140): the project, how many queued tasks it is holding, the free slots
    /// they could be using, and the one lever that releases them. One line per paused project,
    /// so a second paused project is never folded into the first's count.
    /// <para>
    /// Silent when the node has no free slots — work held on a full node is the node ceiling
    /// doing its job, and the queued section below already explains that — and silent when no
    /// measurement is current, since a cap nothing has swept against is not yet a cap the
    /// dispatcher enforced (AGENTS.md: never guess at unobserved facts).
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> PausedProjectLines(
        IReadOnlyList<TaskStatusRow> rows, DispatchPressure? pressure)
    {
        if (pressure is null || pressure.IdleRuns == 0)
        {
            return [];
        }

        return
        [
            .. rows.Where(row => row.Held?.Kind == QueueHoldKind.ProjectPaused)
                .GroupBy(row => row.Project)
                .OrderBy(project => project.Key, StringComparer.OrdinalIgnoreCase)
                .Select(project =>
                    $"[red bold]PAUSED[/] [red]project '{project.Key.EscapeMarkup()}' is holding "
                    + $"{project.Count()} queued task(s) behind a cap of 0 while this node has "
                    + $"{pressure.IdleRuns} free slot(s).[/] [dim]Nothing of this project's starts, and "
                    + "nothing raises the cap on its own:[/] "
                    + $"h9k project set {project.Key.EscapeMarkup()} --max-parallel-tasks <n>"),
        ];
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
    /// Marker first, then oldest assignment, ties broken by when the task was added — exactly
    /// the claim query's own ordering (Decisions Log #64, and the queue-first marker, task
    /// 45136b29, idea fcaded0b's R7 ruling). The queue section tells a human that each of its
    /// rows starts as a run finishes, so its top row has to be the one that starts next;
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
                .OrderByDescending(row => row.QueuePriorityMarked)
                .ThenBy(row => row.AssignedAt ?? DateTimeOffset.MaxValue)
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
