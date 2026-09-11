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
            // Printed before the "nothing tracked" line rather than after the header below,
            // because an install with no tasks at all is exactly where this feature's state is
            // load-bearing (Decisions Log #161): a review request GitHub has made of this login
            // that nothing minted a task for leaves a board with no rows on it and a request
            // waiting, which is the origin incident's own shape.
            await WriteAutoPrReviewAsync(session, rows, DateTimeOffset.UtcNow, cancellationToken);
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

        // One unmissable line while a node-wide launch hold stands (task: a session that exits
        // at once with no work done is treated as the node failing to launch sessions) — every
        // held task's own row still reads waiting-but-handled (AttentionComposer), so this is the
        // one place the episode itself, and the fix, is said out loud.
        try
        {
            LaunchHoldStatus? launchHold = await LaunchHoldStatus.ReadAsync(session, cancellationToken);
            if (launchHold is { Active: true })
            {
                AnsiConsole.MarkupLine(launchHold.NeedsYouLine);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]launch hold: unavailable ({exception.Message})[/]");
        }

        await WriteAutoPrReviewAsync(session, rows, now, cancellationToken);

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
        // A posted review waiting on its author is listed rather than counted for the same reason
        // Delivered work is: it is where "is it my turn yet?" gets answered, and the origin
        // incident this feature exists for (2026-09-08, arx-platform #2023) was a review that
        // went Done and left nothing on the board watching the pull request at all. Each row's
        // phase line names the pull request and how many of the reviewer's threads are still open.
        listed += Section(rows, AttentionBucket.Waiting, "attention-waiting",
            "[cyan]Waiting[/] [dim]— your review is posted; its author has not answered yet[/]", now);
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
        // A fourth cause with the same shape (idea 64c75e43): a project's claim gate is holding a
        // linked task because the tracker does not show its card assigned to this install. Its own
        // trigger rather than a fold into the counted limits', because the lever is per task and
        // lives on the row itself — assign yourself that card — not in one setting for the node or
        // the project, so the heading says only that the rows explain themselves.
        bool heldByTracker = rows.Any(row => row.WaitingForTracker);
        if (atCeiling || atProjectCap || atSpendBudget || heldByTracker)
        {
            int queuedProjects = rows
                .Where(row => row.Group == AttentionBucket.Queued)
                .Select(row => row.ProjectId)
                .Distinct()
                .Count();
            listed += Section(
                rows, AttentionBucket.Queued, "queued",
                QueuedHeading(atCeiling, atProjectCap, atSpendBudget, spend, queuedProjects, heldByTracker), now,
                inServiceOrder: true);
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
    /// Auto pr-review's own state, printed on every run of this pane (Decisions Log #161): one
    /// line per project saying whether a review GitHub requests of this install's own login there
    /// mints a task, and one row per request GitHub is currently making — needs-you where nothing
    /// started and the operator has to act, informational where the daemon is already on it.
    /// <para>
    /// Always printed, at the default as much as at an explicit setting, because the origin
    /// incident was an invisible state rather than a wrong one. Degraded rather than fatal on a
    /// database hiccup, exactly as the spend and capacity lines above are: this pane's job is to
    /// still say something useful when part of the picture is missing.
    /// </para>
    /// </summary>
    private static async Task WriteAutoPrReviewAsync(
        IQuerySession session, IReadOnlyList<TaskStatusRow> rows, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ReviewRequestPaneContents pane;
        try
        {
            pane = await ReviewRequestPane.ComposeAllAsync(session, rows, now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]auto pr-review: unavailable ({exception.Message})[/]");
            return;
        }

        foreach (string line in pane.SettingLines)
        {
            AnsiConsole.MarkupLine(line);
        }

        foreach (ReviewRequestRow request in pane.InReadingOrder)
        {
            AnsiConsole.MarkupLine(request.Markup);
        }
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
    /// <para>
    /// <paramref name="queuedProjects"/> is how many projects the section's rows span, and it
    /// exists to keep one sentence honest (Decisions Log #141). The rows are listed in the claim
    /// query's own order, which is service order <em>within</em> a project — but across projects
    /// the daemon rotates, and its rotation memory is in-process, so no CLI can see whose turn it
    /// is. With more than one project queued the heading says that outright and points at the
    /// daemon log, where every claim names the project that won and why, rather than letting the
    /// top row read as a promise the pane cannot keep.
    /// </para>
    /// <para>
    /// <paramref name="heldByTracker"/> is the one cause here that is not a setting (idea
    /// 64c75e43): a project's claim gate is holding a linked task because the tracker does not
    /// show its card assigned to this install. It contributes a cause and a note pointing at the
    /// rows — each names its own card and who holds it — but no lever, because the fix is done in
    /// the tracker per card, so a queue the gate alone is holding renders no "raise one with"
    /// clause at all.
    /// </para>
    /// </summary>
    internal static string QueuedHeading(
        bool atCeiling,
        bool atProjectCap,
        bool atSpendBudget,
        SpendPressure? spend,
        int queuedProjects = 1,
        bool heldByTracker = false)
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

        // Not a lever like the other three (idea 64c75e43): the claim gate's fix is per task and
        // lives in the tracker, not in any h9k setting, so it earns a cause and a note about what
        // the rows say rather than a line in the levers list — which is also why the levers list
        // can be empty here, on a queue the gate alone is holding.
        if (heldByTracker)
        {
            causes.Add("a project's claim gate is holding a linked task — the tracker does not show its card "
                + "assigned to this install");
        }

        // What the rows themselves can be relied on to say, which depends on whether the spend
        // budget is one of the causes: the two counted limits render their own numbers on the
        // held row, and the budget renders nothing there at all. Null when neither counted limit
        // nor the budget is a cause at all — a queue only the claim gate is holding, where a
        // promise that every row names a limit would send a reader hunting one nothing rendered.
        string? countedNote = (atSpendBudget, atCeiling || atProjectCap) switch
        {
            (true, true) => "Each row below held by one of the two counted limits names it, in that limit's own "
                + "numbers; a row naming none is waiting on the budget, which is this node's alone and has no "
                + "per-task number.",
            (true, false) => "No row below names a limit of its own: the budget is this node's alone and holds "
                + "the whole queue.",
            (false, true) => "Each row below names which limit holds it, in that limit's own numbers.",
            _ => null,
        };

        // Said only when it is true: on a single-project queue the listed order IS the order the
        // dispatcher serves, and a note about a rotation with one member would be noise. Joined
        // rather than interpolated with a space of its own, so the quiet case renders exactly the
        // heading it always did rather than one carrying a stray double space.
        string[] notes =
        [
            .. countedNote is not null ? (string[])[countedNote] : [],
            .. heldByTracker
                ? (string[])
                [
                    "Each row the claim gate holds names its own card and who holds it; assign it to yourself "
                    + "there and the claim proceeds on its own.",
                ]
                : [],
            .. queuedProjects > 1
                ? (string[])
                [
                    $"Rows are oldest first within each project; which of these {queuedProjects} projects takes "
                    + "the next free slot is the daemon's own rotation (longest unserved first, or a --priority "
                    + "tier), and its log names the winner and why on every claim.",
                ]
                : [],
        ];

        // The levers clause is dropped rather than left empty when the claim gate is the only
        // cause: "Raise one with:" followed by nothing reads as a rendering fault, and there is
        // no h9k setting to raise.
        string leverClause = levers.Count > 0
            ? $" Raise one with:[/] {string.Join(" [dim]·[/] ", levers)}"
            : "[/]";
        return $"[blue]Queued[/] [dim]— {string.Join("; ", causes)}. {string.Join(" ", notes)}{leverClause}";
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
    /// 45136b29, idea fcaded0b's R7 ruling). It is service order within one project, and the
    /// honest approximation of it across several: which project takes the next free slot is the
    /// daemon's rotation (Decisions Log #141), whose memory is in-process and so invisible to any
    /// CLI — <see cref="QueuedHeading"/> says so in the section's own words rather than leaving
    /// the top row to imply otherwise. The queue section tells a human that each of its
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
