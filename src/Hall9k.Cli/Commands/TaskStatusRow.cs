using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Run;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One task as every browse and attention surface sees it, carrying the board's three surfaces
/// side by side (Decisions Log #66): <see cref="State"/> is where the work is in its lifecycle,
/// <see cref="Phase"/> is what the machinery is doing right now, and <see cref="Attention"/> is
/// whether a human is wanted and why. <see cref="Facts"/> is the fourth line's worth: what a
/// Published row is waiting for.
/// <para>
/// Composed once, in <see cref="TaskStatusComposer"/>, so a task never reads as Working on one
/// screen and Delivered on the next.
/// </para>
/// </summary>
internal sealed record TaskStatusRow(
    Guid TaskId,
    Guid ProjectId,
    Guid? EpicId,
    LifecycleState State,
    RunState RunState,
    TaskPhase Phase,
    TaskAttention Attention,
    AttentionBucket Group,
    IReadOnlyList<string> Facts,
    string Project,
    string Objective,
    string Type,
    string Activity,
    string PullRequestUrl,
    bool Stalled,
    int Priority,
    DateTimeOffset AddedAt,
    string Assignee = "",
    IReadOnlyList<Guid>? UnmetDependencies = null,
    string? DependencyFailureReason = null,
    /// <summary>
    /// Queued with nowhere to go: this node is at its concurrency ceiling and the numbers that
    /// say so are already on the row's second line (Decisions Log #64) — the derived facts where
    /// the row is Published, the phase line where a pushed follow-up is what is queued. Carried
    /// as its own flag so the attention pane can decide whether the queue is worth a section at
    /// all without reading a sentence back out of a display string.
    /// </summary>
    bool WaitingForSlot = false,
    /// <summary>
    /// When a human assigned the task, null while nothing is assigned. The key the dispatcher
    /// queues on (Decisions Log #64), carried here so a pane listing a deferred queue can list
    /// it in the order it will actually be served.
    /// </summary>
    DateTimeOffset? AssignedAt = null,
    /// <summary>
    /// Whether a human marked this task to take the next free dispatch slot regardless of
    /// assignment age (task 45136b29, idea fcaded0b's R7 ruling) — what the queued section
    /// orders on ahead of <see cref="AssignedAt"/>, mirroring the dispatcher's own claim query.
    /// </summary>
    bool QueuePriorityMarked = false)
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

    /// <summary>
    /// The lifecycle word, and only the lifecycle word. The run vocabulary that used to leak
    /// into this column (Running, UnderReview, ClosingOut, AwaitingReview) is the phase line's
    /// material now — that separation is the whole redesign.
    /// </summary>
    public string StateMarkup => State.Markup;

    public string ProjectMarkup => Project.EscapeMarkup();

    public string TypeMarkup => Type.EscapeMarkup();

    /// <summary>The attention column: needs-you, waiting, or nothing at all.</summary>
    public string AttentionMarkup => Attention.Marker;

    /// <summary>
    /// The line under the row: the phase for live work, the derived facts for a Published row,
    /// and the attention cause and lever whenever there is one. Empty for a row that has nothing
    /// to add, which is what keeps a settled board to one line per task.
    /// <para>
    /// Backlog 36's per-task token line composes in beside this one when it lands; the seam is
    /// that this returns lines rather than a string, so another line joins without a re-layout.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DetailMarkup =>
    [
        .. Phase.HasPhase ? (string[])[Phase.Markup] : [],
        .. Facts.Count > 0 ? (string[])[string.Join(" [dim]·[/] ", Facts.Select(fact => $"[dim]{fact.EscapeMarkup()}[/]"))] : [],
        .. Attention.HasCause ? (string[])[Attention.Markup] : [],
    ];

    /// <summary>
    /// The one detail line a browse surface can afford: the phase where the row has one, the
    /// derived facts where it is Published, and the attention cause where it is neither. Which is
    /// to say the most specific thing this row has to say, since <see cref="DetailMarkup"/> is
    /// already ordered that way.
    /// <para>
    /// The attention pane prints every line because it is a pane of a handful of rows; a list of
    /// twenty tasks that spent three lines each would stop being a list. What the browse surfaces
    /// keep is the distinction they would otherwise have lost with the run vocabulary: three
    /// Published rows that read identically in the Status column say "not assigned", "assigned
    /// and ready" and "waiting on 2 dependencies to close out" underneath it, and the attention
    /// column still says which of them wants a human.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SummaryMarkup => [.. DetailMarkup.Take(1)];

    /// <summary>
    /// Who the task was assigned to, empty when nobody has been. Assignment is the dispatch
    /// trigger (Decisions Log #34), so an empty cell is a fact worth reading: nothing will
    /// claim this task.
    /// </summary>
    public string AssigneeMarkup => Assignee.IsBlank() ? "[dim]—[/]" : Assignee.EscapeMarkup();

    /// <summary>
    /// The objective as the browse and attention surfaces print it. Sanitised before it is
    /// truncated, so the column is measured in characters that will actually be rendered, and
    /// an adopted objective (PLAN.md §3.1a) cannot repaint the table it is listed in.
    /// </summary>
    public string ObjectiveMarkup(int max) =>
        TaskListCommand.Truncate(ExternalText.OneLine(Objective), max).EscapeMarkup();

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
    public static int CellWidth(string markup) => new Segment(markup.RemoveMarkup()).CellCount();

    /// <summary>
    /// The PR as a clickable #number; empty when the task has not opened one. The number is read
    /// by <see cref="PullRequestUrls.ParseNumber"/>, the same reader the phase line and the daemon
    /// use, so the column and the line under it can never name the same row's pull request
    /// differently. A URL that reader cannot get a number out of still renders its link, labelled
    /// "PR" — the row has a pull request either way, and an unparsed URL is a number nobody
    /// observed rather than a number to invent.
    /// <para>
    /// The URL was observed from GitHub rather than authored here, so the link target is escaped:
    /// a bare '[' or ']' in it (an IPv6 host, say) would otherwise derail Spectre's markup parser
    /// and take the whole table down. Spectre unescapes the target again when it emits the
    /// hyperlink, so the link still points exactly where the URL was observed to point. The label
    /// needs no escaping — it is a number this code produced, or the two letters "PR".
    /// </para>
    /// </summary>
    public string PullRequestMarkup => PullRequestUrl.IsNotBlank()
        ? $"[link={PullRequestUrl.EscapeMarkup()}]{PullRequestLabel}[/]"
        : string.Empty;

    private string PullRequestLabel => PullRequestUrls.ParseNumber(PullRequestUrl) is int number and > 0
        ? $"#{number}"
        : "PR";

    public string AgeMarkup(DateTimeOffset now) => TaskStatusComposer.RelativeAge(now - AddedAt);
}

/// <summary>
/// The coarse grouping the rollups count and h9k status groups by. An in-process display
/// outcome, never persisted (AGENTS.md: enums only for unpersisted in-process outcomes) —
/// the persisted vocabularies are TaskState and RunState, and the displayed lifecycle
/// vocabulary is <see cref="LifecycleState"/>.
/// </summary>
internal enum AttentionBucket
{
    /// <summary>Parked on a question, a review verdict, a dead blocker, or a failure: it waits on a human.</summary>
    NeedsYou,

    /// <summary>Live work whose session has gone quiet, or whose recorded process is gone.</summary>
    Stalled,

    /// <summary>
    /// A run owns the task and has not pushed yet: building, gating, or in the pre-PR review
    /// loop. Also the dispatch handoff itself — a claim whose run document has not appeared yet,
    /// or whose run has already ended — because the platform still holds the claim there.
    /// </summary>
    Working,

    /// <summary>
    /// Pushed, merge not observed: an open pull request being watched, and any follow-up run
    /// driving it. Named after the lifecycle state it counts, so the group and the column agree.
    /// </summary>
    Delivered,

    /// <summary>Assigned with every dependency met: the dispatcher has not claimed it yet.</summary>
    Queued,

    /// <summary>The objective was met and the merge was observed.</summary>
    Done,

    /// <summary>Assigned, but waiting on a dependency that has not reached true closeout.</summary>
    Blocked,

    /// <summary>Published and past the readiness gate: waiting for a human to assign it.</summary>
    Ready,

    /// <summary>Still being developed: a draft, invisible to the dispatcher until published.</summary>
    Draft,

    /// <summary>Archived, or a state this build does not recognize.</summary>
    Closed,
}
