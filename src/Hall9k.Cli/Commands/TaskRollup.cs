namespace Hall9k.Cli.Commands;

/// <summary>
/// A set of tasks counted by attention bucket. The buckets are single-assignment, so the
/// counts always sum to <see cref="Total"/> — a project's row on h9k project list adds up
/// to its task count with nothing hidden between the columns.
/// </summary>
internal sealed record TaskRollup(
    int NeedsYou,
    int Stalled,
    int Active,
    int InReview,
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
        ["Needs you", "Stalled", "Active", "In review", "Queued", "Blocked", "Ready", "Draft", "Done", "Closed"];

    public int Total => NeedsYou + Stalled + Active + InReview + Queued + Blocked + Ready + Draft + Done + Closed;

    public static TaskRollup From(IEnumerable<TaskStatusRow> rows)
    {
        TaskRollup rollup = Empty;
        foreach (TaskStatusRow row in rows)
        {
            rollup = row.Attention switch
            {
                AttentionBucket.NeedsYou => rollup with { NeedsYou = rollup.NeedsYou + 1 },
                AttentionBucket.Stalled => rollup with { Stalled = rollup.Stalled + 1 },
                AttentionBucket.Active => rollup with { Active = rollup.Active + 1 },
                AttentionBucket.InReview => rollup with { InReview = rollup.InReview + 1 },
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
        Cell(Active, "yellow"),
        Cell(InReview, "magenta"),
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
        Add(parts, Active, "yellow", "active");
        Add(parts, InReview, "magenta", "in review");
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
