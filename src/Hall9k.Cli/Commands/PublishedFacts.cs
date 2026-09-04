using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The derived-facts line under a Published row (Decisions Log #66). The Status column says
/// Published and stops; everything that distinguishes one published task from another —
/// unassigned, assigned and not yet claimed, waiting on blockers — is a fact composed here.
/// <para>
/// This is what lets the persisted model stay untouched in this pass. Queued and Blocked keep
/// their streams and their transitions; they simply stop being lifecycle words a human reads and
/// become facts on this line. When the ranking model retires them (IDEA-ranking-and-grooming),
/// only this composer changes.
/// </para>
/// <para>
/// <b>The ranking slot.</b> The line is an ordered list of facts rather than a sentence
/// precisely so ranking facts can join it without a second redesign: unranked / ranked /
/// expedited, and available / held, are appended here beside the dispatch facts when the ranking
/// model records them. Nothing about ranking is built now and no unranked count is reported
/// anywhere — the column exists, the facts arrive later.
/// </para>
/// </summary>
internal static class PublishedFacts
{
    private const string QueuePriorityFact =
        "marked queue-first — takes the next free dispatch slot regardless of assignment age";

    /// <summary>
    /// What a Published row is actually waiting for, oldest question first: whether a human has
    /// assigned it at all, then what the platform is waiting on. Empty for any row that is not
    /// Published, which is what keeps the line from appearing where it would say nothing — except
    /// for the queue-first marker (task 45136b29), which is stated wherever it is set, Published
    /// or not, since the decider allows setting it on a currently-Claimed task too.
    /// </summary>
    /// <param name="heldByCeiling">
    /// The measurement that says this row is waiting on a dispatch slot, or null when nothing
    /// measured one (<see cref="DispatchPressure"/>, Decisions Log #64). A queued row says it is
    /// ready and stops there without it: the platform cannot see why the dispatcher has not
    /// claimed it yet, and a daemon that is simply stopped is the commonest reason of all.
    /// </param>
    public static IReadOnlyList<string> Compose(
        TaskListItem task, LifecycleState state, DispatchPressure? heldByCeiling = null)
    {
        if (state != LifecycleState.Published)
        {
            // The marker can be set while the decider allows it — Queued, Blocked, even a
            // currently-Claimed task, for its next turn in the queue (Decisions Log #127) — and
            // a currently-Claimed task reads as LifecycleState.Working, not Published. Without
            // this, a human who marks a running task queue-first sees the marker recorded on the
            // stream but nowhere on the board until it lands back on Queued or Blocked
            // (independent pre-PR review, cycle 1, conformance lens).
            return task.QueuePriorityMarked ? [QueuePriorityFact] : [];
        }

        IReadOnlyList<string> facts = task.State.Value switch
        {
            "Published" => ["not assigned — nothing will claim it until you assign it"],
            // Ready is all this row can honestly claim on its own: it is assigned, its
            // dependencies are met, and the dispatcher has not claimed it. Why not is a question
            // only a measurement can answer, so the slot line is appended when one exists and
            // omitted when none does, rather than a contention being asserted from the state
            // alone (AGENTS.md, the never-guess rule).
            "Queued" =>
            [
                "assigned and ready; the dispatcher has not claimed it yet",
                .. heldByCeiling is not null ? (string[])[heldByCeiling.ReasonLine] : [],
            ],
            // A blocker recorded dead is answered before the count, in the same words and the
            // same order its phase-line twin uses (TaskPhaseComposer.BlockedDetail). A death
            // leaves UnmetDependencies untouched — TaskDependencyFailed only appends to the dead
            // list — so the count arm below would report a wait the stream says will never end,
            // and on the browse surfaces that single line is the whole of what the row says.
            // The recorded death itself stays on the attention line, which quotes it whole.
            "Blocked" when task.DependencyFailureReason.IsNotBlank() =>
                ["a blocker will not close out on its own", .. BlockedBy(task)],
            // A Blocked task with nothing recorded as unmet is a record disagreeing with itself,
            // so the line says that rather than reporting a wait on zero things.
            "Blocked" when task.UnmetDependencies.Count == 0 =>
                ["blocked, but no unmet dependency is recorded"],
            "Blocked" =>
            [
                $"waiting on {task.UnmetDependencies.Count} dependenc"
                    + $"{(task.UnmetDependencies.Count == 1 ? "y" : "ies")} to close out",
                .. BlockedBy(task),
            ],
            // A published task in a state this build does not recognize says so rather than
            // being described as one of the states it might be.
            _ => [$"published; the recorded state ({task.State.Value}) is not one this build knows"],
        };

        // The marker matters most on a Queued row (it is what the claim query orders on), but
        // it is stated wherever it is set — including Blocked, where it is inert until the
        // blocker clears — so a human never has to guess whether a marker they set survived
        // (task 45136b29, idea fcaded0b's R7 ruling).
        return task.QueuePriorityMarked ? [.. facts, QueuePriorityFact] : facts;
    }

    /// <summary>
    /// Which blockers, named as the reader can type them, and nothing at all when none is
    /// recorded — because a dead blocker already cleared out of the unmet list leaves this the
    /// choice between an empty list rendered as "blocked by" and saying nothing.
    /// </summary>
    private static IReadOnlyList<string> BlockedBy(TaskListItem task) =>
        task.UnmetDependencies.Count == 0
            ? []
            : [$"blocked by {string.Join(", ", task.UnmetDependencies.Select(TaskListCommand.ShortId))}"];
}
