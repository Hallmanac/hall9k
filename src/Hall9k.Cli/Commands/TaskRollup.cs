namespace Hall9k.Cli.Commands;

/// <summary>
/// A set of tasks counted by attention bucket. The buckets are single-assignment, so the
/// counts always sum to <see cref="Total"/> — a project's row on h9k project list adds up
/// to its task count with nothing hidden between the columns.
/// <para>
/// The columns follow the displayed lifecycle vocabulary (Decisions Log #66): what used to be
/// counted as Active is Working, and what used to be In review is Delivered, so a count and the
/// Status column beside it read the same word.
/// </para>
/// </summary>
internal sealed record TaskRollup(
    int NeedsYou,
    int Stalled,
    int Working,
    int Delivered,
    int Queued,
    int Blocked,
    int Ready,
    int Draft,
    int Done,
    int Closed)
{
    public static readonly TaskRollup Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Column headers, in the same order as <see cref="Cells"/>.</summary>
    public static readonly string[] Columns =
        ["Needs you", "Stalled", "Working", "Delivered", "Queued", "Blocked", "Ready", "Draft", "Done", "Closed"];

    public int Total => NeedsYou + Stalled + Working + Delivered + Queued + Blocked + Ready + Draft + Done + Closed;

    public static TaskRollup From(IEnumerable<TaskStatusRow> rows)
    {
        TaskRollup rollup = Empty;
        foreach (TaskStatusRow row in rows)
        {
            rollup = row.Group switch
            {
                AttentionBucket.NeedsYou => rollup with { NeedsYou = rollup.NeedsYou + 1 },
                AttentionBucket.Stalled => rollup with { Stalled = rollup.Stalled + 1 },
                AttentionBucket.Working => rollup with { Working = rollup.Working + 1 },
                AttentionBucket.Delivered => rollup with { Delivered = rollup.Delivered + 1 },
                // Counted under Delivered rather than earning a column of its own (task: a
                // pr-review task stays open while the pull request's review threads are
                // unresolved). The rollup is the coarse view — one row per project on
                // h9k project list — and both groups mean the identical coarse thing there: an
                // open pull request this install is watching. The distinction that matters (whose
                // pull request, and what the wait is for) is what the Status column and the
                // h9k status section carry, where there is room to say it. Explicit rather than
                // left to fall through, because the fall-through is Closed, which would count a
                // live wait as archived and quietly break "the columns sum to the task count".
                AttentionBucket.Waiting => rollup with { Delivered = rollup.Delivered + 1 },
                AttentionBucket.Queued => rollup with { Queued = rollup.Queued + 1 },
                AttentionBucket.Blocked => rollup with { Blocked = rollup.Blocked + 1 },
                AttentionBucket.Ready => rollup with { Ready = rollup.Ready + 1 },
                AttentionBucket.Draft => rollup with { Draft = rollup.Draft + 1 },
                AttentionBucket.Done => rollup with { Done = rollup.Done + 1 },
                _ => rollup with { Closed = rollup.Closed + 1 },
            };
        }

        return rollup;
    }

    /// <summary>Counts as table cells, coloured by bucket; a zero is a dim dot, not a loud 0.</summary>
    public string[] Cells =>
    [
        Cell(NeedsYou, "red bold"),
        Cell(Stalled, "red"),
        Cell(Working, "yellow"),
        Cell(Delivered, "magenta"),
        Cell(Queued, "blue"),
        Cell(Blocked, "cyan"),
        Cell(Ready, "blue"),
        Cell(Draft, "dim"),
        Cell(Done, "green"),
        Cell(Closed, "dim"),
    ];

    /// <summary>
    /// The same counts as one glanceable sentence, naming only the buckets that have
    /// anything in them — the header line of h9k status and h9k project show.
    /// </summary>
    public string Summary()
    {
        List<string> parts = [];
        Add(parts, NeedsYou, "red bold", "need you");
        Add(parts, Stalled, "red", "stalled");
        Add(parts, Working, "yellow", "working");
        Add(parts, Delivered, "magenta", "delivered");
        Add(parts, Queued, "blue", "queued");
        Add(parts, Blocked, "cyan", "blocked");
        Add(parts, Ready, "blue", "ready to assign");
        Add(parts, Draft, "dim", "draft");
        Add(parts, Done, "green", "done");
        Add(parts, Closed, "dim", "closed");

        return parts.Count > 0 ? string.Join(" · ", parts) : "[dim]no tasks[/]";
    }

    private static void Add(List<string> parts, int count, string colour, string label)
    {
        if (count > 0)
        {
            parts.Add($"[{colour}]{count} {label}[/]");
        }
    }

    private static string Cell(int count, string colour) =>
        count == 0 ? "[dim]·[/]" : $"[{colour}]{count}[/]";
}
