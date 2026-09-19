namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// This run was meant to resume a previous attempt's branch and could not: the branch existed
/// neither locally nor on origin, so the run was cut fresh from <paramref name="BaseBranch"/>
/// instead and whatever the previous attempt built is not in it.
/// <para>
/// Recorded rather than only logged because it is the one fact that explains an otherwise
/// puzzling run: a task whose history says "resumed" carrying a worktree with none of the
/// resumed work in it. <c>h9k task show</c> reads it back off
/// <see cref="Projections.RunDetails.StartedCleanAfterGoneBranch"/> and says so in the run's own
/// coordinates, so nobody has to reconstruct it from daemon logs on a node that may not even be
/// the node that lost the work.
/// </para>
/// <para>
/// Origin incident (2026-09-19 14:33 and 14:35 EDT, task a56cf16e, runs 01a0baf1 and 01a0baf3):
/// <c>h9k task take --force</c> took a task from a Mac node whose run had built eleven minutes
/// without pushing, and the two retries that followed failed at launch on a branch that had never
/// left that Mac. Starting clean is the honest outcome there; saying nothing about it would not be
/// (AGENTS.md's never-guess rule cuts both ways — the gap gets named, not papered over).
/// </para>
/// </summary>
/// <param name="Id">The run this happened to.</param>
/// <param name="GoneBranch">The branch the run tried to resume and could not find on either side.</param>
/// <param name="BaseBranch">The branch the fresh worktree was actually cut from instead.</param>
/// <param name="Reason">The worktree layer's own sentence for why the resume failed, carried verbatim.</param>
/// <param name="ResumedForeignNode">
/// Whether the branch belonged to another node's own run (<c>TaskAggregate.RetryBranchResumesForeignNode</c>)
/// rather than to an earlier attempt on this one. Both start clean, but only the first means work
/// that exists somewhere this node cannot reach — which is the difference between "an old worktree
/// was cleaned up" and "somebody else's eleven minutes are still on their laptop".
/// </param>
/// <param name="At">When the fresh cut was made.</param>
public sealed record RunStartedCleanAfterBranchGone(
    Guid Id,
    string GoneBranch,
    string BaseBranch,
    string Reason,
    bool ResumedForeignNode,
    DateTimeOffset At);
