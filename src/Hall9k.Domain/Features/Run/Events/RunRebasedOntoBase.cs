namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The run checked, and if needed rebased, its branch onto the project's current base branch
/// immediately before the mandatory final full pass (task: a run rebases its branch onto the
/// current base branch), so the pull request it opens is mergeable on arrival rather than
/// racing a merge that landed while the run was building. Informational only, like
/// <see cref="PullRequestMechanicalRebaseAttempted"/> — it never moves <see cref="RunState"/>
/// or <see cref="ReviewPhase"/> on its own; a clean or no-op outcome leaves the loop exactly
/// where it was, so the very next check reads the (possibly rebased) worktree with no extra
/// wiring.
/// <para>
/// A clean rebase costs no review of its own (Brian's 2026-09-04 ruling): git applying every
/// commit without a conflict is itself the evidence that no judgment was exercised, so this
/// event alone never triggers an extra Discovery cycle, lens, or fix session — the mandatory
/// final gate and pass that were already about to run simply read the tree this event just
/// produced. A conflicting attempt never reaches this event at all: it is followed by
/// <see cref="PreFinalPassRebaseRecoveryDispatched"/> instead, and this event is appended only
/// once that recovery session (or a later resolved retry of it) actually resolves the conflict.
/// </para>
/// </summary>
/// <param name="RebasedFromCommit">
/// The base commit this branch was built against before this attempt — <c>git merge-base HEAD
/// origin/&lt;base&gt;</c>, read after the fetch. Ordinarily equal to
/// <paramref name="RebasedOntoCommit"/> only when <paramref name="WasNoOp"/> is true, but a
/// recovery session's own trusted-at-face-value <c>RESOLUTION: fixed</c> claim (see
/// <c>RecordRebaseRecoveryResultAsync</c>) can also produce an equal pair with
/// <paramref name="WasNoOp"/> false and <see cref="RecoveredByAgentSession"/> true, when the
/// worktree never actually moved despite the claim — the audit record then says "recovered" over
/// a from/onto pair identical to a no-op's, which is the honest reflection of an unconfirmed claim
/// rather than a violated invariant.
/// </param>
/// <param name="RebasedOntoCommit">The base branch's freshly fetched tip this branch is now built against.</param>
/// <param name="WasNoOp">
/// True when origin's base branch had not moved past what this branch already contained — the
/// step added nothing to the run (task: the no-op guarantee).
/// </param>
/// <param name="RecoveredByAgentSession">
/// True when a plain <c>git rebase</c> conflicted and a narrow recovery session resolved it with
/// judgment; false for a no-op or a rebase git applied cleanly on its own.
/// </param>
/// <param name="Detail">What actually happened, readable from <c>h9k task show</c>.</param>
public sealed record RunRebasedOntoBase(
    Guid Id,
    string RebasedFromCommit,
    string RebasedOntoCommit,
    bool WasNoOp,
    bool RecoveredByAgentSession,
    string Detail,
    DateTimeOffset RebasedAt)
{
    /// <summary>
    /// What <see cref="RebasedFromCommit"/> and <see cref="RebasedOntoCommit"/> carry when the read
    /// that would have named a commit failed — an admitted gap in an audit field rather than a
    /// guessed commit (AGENTS.md's never-guess rule), written by
    /// <c>ReviewEngine.ResolveObservedOntoCommitAsync</c> and its dispatch-time sibling. Named here
    /// so a reader deciding whether the pair is a commit at all cannot drift from the writer.
    /// </summary>
    public const string UnreadableCommit = "unknown";

    /// <summary>
    /// Whether <see cref="RebasedOntoCommit"/> actually names an observed commit — the gate on
    /// treating it as this branch's new fork point (see <c>RunAggregate.Apply</c>'s own comment).
    /// </summary>
    public bool OntoCommitObserved =>
        RebasedOntoCommit.IsNotBlank() && RebasedOntoCommit != UnreadableCommit;
}
