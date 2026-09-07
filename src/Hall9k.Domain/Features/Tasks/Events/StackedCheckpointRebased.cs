namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A stacked child spent one unit of its rebase budget catching up to its parent's current head at
/// one of its own checkpoints (task: a stacked child absorbs its parent's post-delivery churn
/// safely) — the in-run half of the same budget closeout's own replay follow-ups spend
/// (<see cref="TaskAggregate.StackReplaysDispatched"/>, <c>DaemonOptions.MaxStackReplayRuns</c>).
/// One budget for both, deliberately: a replay dispatched after the pull request opened and a
/// checkpoint rebase taken before it ever did are the same operation answering the same cause — the
/// parent's branch moving — and splitting the count would let a parent that will not stop moving
/// spend two caps instead of one before anybody is told.
/// <para>
/// Recorded on the TASK's stream rather than the run's because the budget is the task's: a child's
/// churn spans its build run and every follow-up run after it, and a counter that lived on the run
/// would restart at zero on each one. The mechanical outcome of the rebase itself — what moved,
/// from where, onto what — is recorded on the run's stream as
/// <c>Hall9k.Domain.Features.Run.Events.RunRebasedOntoBase</c>, which is also what advances the
/// run's own recorded fork point; this event carries only what the budget needs plus enough
/// identity to read it back against that one.
/// </para>
/// <para>
/// Reset by <see cref="TaskAggregate.ResetAutomaticCloseoutState"/> like every other automatic
/// budget: a human's own <c>h9k pr resolve</c> refills the pool.
/// </para>
/// </summary>
/// <param name="RunId">The run whose checkpoint this was — the same run <c>RunRebasedOntoBase</c> landed on.</param>
/// <param name="Checkpoint">Which of the child's two defined checkpoints this was taken at.</param>
/// <param name="ParentBranch">
/// The parent branch this child caught up to, as this run recorded it. Named here rather than left
/// to be re-derived: the record is cleared to blank the moment the child is retargeted onto the
/// project's base, and by then this event still has to say what it was about.
/// </param>
/// <param name="UpstreamCommit">
/// The replay's own <c>&lt;upstream&gt;</c> — the commit everything at or before which belonged to
/// the parent, which is what the replay drops rather than re-applies. Always the run's recorded
/// fork point, never <c>git merge-base</c>: see <c>StackedParentWatch</c>'s own doc for the
/// force-push case that proves merge-base wrong here.
/// </param>
/// <param name="OntoCommit">The freshly observed commit the child's own commits were replayed onto.</param>
public sealed record StackedCheckpointRebased(
    Guid Id,
    Guid RunId,
    StackedCheckpoint Checkpoint,
    string ParentBranch,
    string UpstreamCommit,
    string OntoCommit,
    DateTimeOffset RebasedAt);
