namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// This node force-with-lease pushed <paramref name="Branch"/> to origin and landed at
/// <paramref name="Tip"/> — the durable half of the force-with-lease push guard fix (origin
/// incident, 2026-09-15: task 62acc347 run 01a0a376 and task 6189c968 run 01a0a3a8 both failed at
/// push because a follow-up lap's history surgery in the shared repository (<c>git filter-branch</c>
/// plus <c>git reflog expire --all</c>) wiped every branch's reflog on the node, including one whose
/// own previous run had pushed the exact tip origin still held).
/// <para>
/// Recorded on the TASK's stream, not the run's: the guard has to recognize a tip a PRIOR run of
/// this same task pushed, and a run's own stream ends with that run. <see cref="TaskAggregate.LastPushedBranch"/>
/// and <see cref="TaskAggregate.LastPushedBranchTip"/> are what
/// <c>Hall9k.Daemon.Execution.PullRequestOpener</c> reads back before its own next push and hands to
/// <c>Hall9k.Daemon.Execution.ForceWithLeasePusher</c> as a third way to call origin's tip safe,
/// alongside the ancestor and reflog checks that already existed — recognizing this node's own
/// history even when the reflog that used to prove it has since been expired out from under it.
/// </para>
/// <para>
/// Appended best-effort, immediately after a successful push and before anything else that push
/// enables (opening or updating the pull request, completing the task): a failure to record this
/// fact must never fail a push that already landed on origin, so a session that cannot write it logs
/// a warning and carries on. The ancestor and reflog checks still cover the ordinary case either way;
/// this only closes the gap the origin incident found.
/// </para>
/// </summary>
/// <param name="Branch">The branch this node just pushed.</param>
/// <param name="Tip">The commit that push landed at — this node's own local HEAD at push time.</param>
/// <param name="PushedAt">When the push completed.</param>
public sealed record TaskBranchPushed(Guid Id, string Branch, string Tip, DateTimeOffset PushedAt);
