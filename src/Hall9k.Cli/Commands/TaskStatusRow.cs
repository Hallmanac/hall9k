using Spectre.Console;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One task as every browse and attention surface sees it: the composed display bucket
/// (TaskStatusComposer.Compose), plus the raw values a caller filters and sorts on. The
/// markup members are the shared rendering, so the Status column reads identically in
/// h9k status, h9k task list, and h9k project show.
/// </summary>
internal sealed record TaskStatusRow(
    Guid TaskId,
    Guid ProjectId,
    string Bucket,
    string StatusMarkup,
    AttentionBucket Attention,
    string Project,
    string Objective,
    string Type,
    string Activity,
    string PullRequestUrl,
    bool Stalled,
    int Priority,
    DateTimeOffset AddedAt)
{
    /// <summary>
    /// A truncated objective still has to say something; below this the column is noise. A
    /// console too narrow to pay for even this lets the row wrap, which is the better of two
    /// bad readings.
    /// </summary>
    private const int MinObjective = 24;

    /// <summary>Past this the eye stops scanning a column and starts reading a paragraph.</summary>
    private const int MaxObjective = 72;

    public string ShortId => TaskListCommand.ShortId(TaskId);

    public string IdMarkup => $"[dim]{ShortId}[/]";

    public string ProjectMarkup => Project.EscapeMarkup();

    public string TypeMarkup => Type.EscapeMarkup();

    public string ObjectiveMarkup(int max) => TaskListCommand.Truncate(Objective, max).EscapeMarkup();

    /// <summary>
    /// How much objective a row can carry and stay scannable: whatever the console has left
    /// once the fixed columns beside it, and the table's own frame, are paid for. A wide
    /// terminal shows more of the sentence; a narrow one truncates rather than wrapping every
    /// row over four lines. Measured from the cells that are about to be rendered rather than
    /// budgeted at a constant, because a budget that undercounts hands the objective more
    /// characters than its column is given and every long row wraps to two — the stacking the
    /// truncation exists to prevent. Origin incident (2026-08-20): the first cut of these tables
    /// budgeted the fixed columns at a constant, 56 and 46 against real costs of 67 and 57, and
    /// pre-PR review found every long objective wrapping to a second line at every terminal
    /// width; a constant could not have been right anyway, since a column is as wide as the
    /// longest project name or status word in the rows on screen.
    /// </summary>
    /// <param name="consoleWidth">The width the table will be rendered at.</param>
    /// <param name="bordered">Whether the table draws a border, which is chrome of its own.</param>
    /// <param name="fixedColumns">
    /// Every column beside the objective, each as the markup of its header followed by the
    /// markup of its cells. The header counts even when the table hides it: Spectre still
    /// sizes the column to fit it.
    /// </param>
    public static int ObjectiveWidth(
        int consoleWidth, bool bordered, IReadOnlyList<IReadOnlyList<string>> fixedColumns)
    {
        int content = fixedColumns.Sum(column => column.Max(CellWidth));
        int columns = fixedColumns.Count + 1;

        // Measured against Spectre 0.55 rather than reasoned about: a bordered table charges
        // three cells per column and one more for the closing edge (a space of padding on each
        // side of every cell, plus a border character at both edges and every boundary), and a
        // borderless one charges two per column — it still reserves the border cells it then
        // declines to draw, so the rendered row comes out narrower than the width it was
        // budgeted. TaskTableLayoutTests renders every surface and fails if either drifts.
        int chrome = bordered ? (3 * columns) + 1 : 2 * columns;
        return Math.Clamp(consoleWidth - content - chrome, MinObjective, MaxObjective);
    }

    /// <summary>What one cell costs the layout: the terminal cells its markup renders to.</summary>
    private static int CellWidth(string markup) => new Segment(markup.RemoveMarkup()).CellCount();

    /// <summary>
    /// The PR as a clickable #number; empty when the task has not opened one. The URL was
    /// observed from GitHub rather than authored here, so the link target and the number read
    /// off it are both escaped: a bare '[' or ']' in the URL (an IPv6 host, say) would
    /// otherwise derail Spectre's markup parser and take the whole table down with it.
    /// Spectre unescapes the target again when it emits the hyperlink, so the link still
    /// points exactly where the URL was observed to point.
    /// </summary>
    public string PullRequestMarkup => PullRequestUrl.IsNotBlank()
        ? $"[link={PullRequestUrl.EscapeMarkup()}]#{PullRequestNumber.EscapeMarkup()}[/]"
        : string.Empty;

    private string PullRequestNumber => PullRequestUrl[(PullRequestUrl.LastIndexOf('/') + 1)..];

    public string AgeMarkup(DateTimeOffset now) => TaskStatusComposer.RelativeAge(now - AddedAt);
}

/// <summary>
/// The coarse grouping the rollups count and h9k status groups by. An in-process display
/// outcome, never persisted (AGENTS.md: enums only for unpersisted in-process outcomes) —
/// the persisted vocabularies are TaskState and RunState.
/// </summary>
internal enum AttentionBucket
{
    /// <summary>Parked on a question, a review verdict, or a failure: it waits on a human.</summary>
    NeedsYou,

    /// <summary>Claimed and live, but the agent stream has gone quiet past the stall threshold.</summary>
    Stalled,

    /// <summary>
    /// Dispatched, running, verifying, under review, or closing out: work is moving. Also the
    /// dispatch handoff itself — a claim whose run document has not appeared yet, or whose run
    /// has already ended — because the platform still holds the claim there.
    /// </summary>
    Active,

    /// <summary>
    /// The pull request is open and waiting on review, checks, or a merge: the AwaitingReview,
    /// ChecksFailing, and ReviewPending buckets together. Named in-review rather than after the
    /// AwaitingReview bucket it contains, so the group and that one state stay tellable apart
    /// wherever they are typed or counted (TaskStateFilter carries the origin incident).
    /// </summary>
    InReview,

    /// <summary>Queued for the daemon to claim.</summary>
    Queued,

    /// <summary>The objective was met.</summary>
    Done,

    /// <summary>Abandoned, or a state this build does not recognize.</summary>
    Closed,
}
