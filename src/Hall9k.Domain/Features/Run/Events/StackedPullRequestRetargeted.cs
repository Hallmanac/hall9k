namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A stacked child's pull request was retargeted on GitHub, from the parent's branch onto the
/// project's own base branch, because closeout observed the parent's pull request merge (task: a
/// stacked pull-request edge exists as an explicit opt-in dependency). Recorded on the CHILD's run
/// stream, by whichever node observed the parent's merge.
/// <para>
/// Informational, like <see cref="PullRequestMechanicalRebaseAttempted"/>: it never moves
/// <see cref="RunState"/> or <see cref="ReviewPhase"/> on its own. The stacked replay dispatched
/// alongside it is what actually moves this run on, and that arrives as the ordinary
/// <c>TaskReopened</c> + <see cref="RunSuperseded"/> pair every automatic follow-up uses.
/// </para>
/// <para>
/// Appended for a <em>failed</em> retarget too, with <paramref name="Succeeded"/> false: the whole
/// point of the record is that this child's pull request was, or was not, actually moved off a
/// branch that no longer exists, and a silent failure would leave a pull request aimed at a deleted
/// base with nothing on the stream to say why (AGENTS.md: the unobserved is admitted, never
/// guessed). A failed attempt dispatches no replay — the next sweep tries the retarget again.
/// </para>
/// </summary>
/// <param name="FromBase">The base the pull request carried before this attempt — the parent's branch.</param>
/// <param name="ToBase">The base it was retargeted onto: the project's own base branch.</param>
/// <param name="BoundaryCommit">
/// The commit everything at or before which belongs to the parent, as this sweep observed it — the
/// <c>&lt;upstream&gt;</c> the replay drops the parent's commits at. That is the parent branch's own
/// head when the child still contains it (a parent that merged without ever being rewritten), and
/// the child's recorded fork point when it does not; <c>StackedParentWatch</c>'s own doc has why
/// those are the only two honest answers. Named for the role it plays rather than for one of the
/// two things it can be, because a reader following the field has to know what it means when the
/// parent DID advance past the child's cut point before merging (independent pre-PR review, cycle
/// 1, conformance lens). Empty when no boundary could be observed, which is why the replay refuses
/// to dispatch rather than guessing one.
/// </param>
/// <param name="Detail">What actually happened, readable from <c>h9k task show</c>.</param>
public sealed record StackedPullRequestRetargeted(
    Guid Id,
    string FromBase,
    string ToBase,
    string BoundaryCommit,
    bool Succeeded,
    string Detail,
    DateTimeOffset RetargetedAt);
