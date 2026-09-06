using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Which measured limit is holding a queued row back, and the sentence that says so. One type for
/// both counted limits, because a row has exactly one answer to "why has this not started" and
/// the two levers are different commands: the node's ceiling
/// (<c>h9k config set --max-concurrent-task-runs</c>, Decisions Log #64, #111) and the project's
/// own cap (<c>h9k project set --max-parallel-tasks</c>, #140).
/// <para>
/// The spend budget is deliberately not one of them. It is a single node-wide figure with no
/// per-task denominator, so there is no row-level sentence to compose in the numbers that caused
/// it; the queued section's own heading carries it instead, and says plainly that a row naming no
/// limit is one the budget is holding (<c>StatusCommand.QueuedHeading</c>). The dispatcher asks
/// the project's cap ahead of the budget for the matching reason
/// (<c>DispatchEngine.ClaimEligibleAsync</c>): a rolled-over period releases nothing a project's
/// own cap is holding.
/// </para>
/// <para>
/// Null everywhere it appears means the platform measured nothing that explains the wait — no
/// daemon has swept recently, or neither limit is full — and every surface then says nothing
/// about slots rather than inventing a contention nobody observed (AGENTS.md, the never-guess
/// rule). A daemon that is simply stopped is the commonest reason of all, and the pane's own
/// banner already covers it.
/// </para>
/// </summary>
/// <param name="Kind">Which limit it is, so a section can group and count by cause.</param>
/// <param name="ReasonLine">
/// The row's own sentence, in the numbers that caused it. Plain text, never markup: every caller
/// escapes it before rendering, because a project name reaches it verbatim.
/// </param>
/// <param name="Project">The project this row belongs to, named for the cap's own lever.</param>
internal sealed record QueueHold(QueueHoldKind Kind, string ReasonLine, string Project)
{
    /// <summary>
    /// The one resolution order both surfaces use, matching the dispatcher's own
    /// (<c>Hall9k.Daemon.Dispatch.DispatchEngine.ClaimEligibleAsync</c>): the project's cap is
    /// asked before the node's ceiling, so a task its project is holding back is never reported
    /// against a node ceiling that raising would not release, and a paused project is told apart
    /// from a full one because only one of them is answered by a bigger number.
    /// <para>
    /// Read off the task's persisted state rather than a composed display group, because the
    /// dispatcher reads that state too: every Queued task faces both ceilings, including a
    /// closeout follow-up the monitor reopened, which the display groups under Delivered.
    /// </para>
    /// </summary>
    public static QueueHold? For(TaskListItem task, string project, DispatchPressure? pressure)
    {
        if (task.State != TaskState.Queued || pressure is null)
        {
            return null;
        }

        ProjectRunCeiling? ceiling = pressure.ForProject(task.ProjectId);
        return ceiling switch
        {
            { IsPaused: true } => new QueueHold(
                QueueHoldKind.ProjectPaused,
                $"held — project '{project}' is paused at a cap of 0 run(s); nothing of its own starts, however "
                + $"idle this node is: h9k project set {project} --max-parallel-tasks <n>",
                project),
            { OverCap: true } => new QueueHold(
                QueueHoldKind.ProjectCap,
                $"waiting for a slot — project cap {ceiling.LiveRuns} running, over a cap of {ceiling.Cap}",
                project),
            { AtCap: true } => new QueueHold(
                QueueHoldKind.ProjectCap,
                $"waiting for a slot — project cap {ceiling.LiveRuns} of {ceiling.Cap} running",
                project),
            _ => pressure.AtCeiling
                ? new QueueHold(QueueHoldKind.NodeCeiling, pressure.ReasonLine, project)
                : null,
        };
    }
}

/// <summary>
/// Which limit a <see cref="QueueHold"/> names. An in-process display outcome, never persisted
/// (AGENTS.md: enums only for unpersisted in-process outcomes) — what is persisted is the
/// measurement itself (<c>NodeDispatchLoad</c>) and the cap on the project's own stream.
/// </summary>
internal enum QueueHoldKind
{
    /// <summary>This node is at (or over) its own concurrency ceiling.</summary>
    NodeCeiling,

    /// <summary>The row's project is at (or over) its own run cap, whatever the node has free.</summary>
    ProjectCap,

    /// <summary>The row's project is capped at 0: deliberately paused, and nothing raises it on its own.</summary>
    ProjectPaused,
}
