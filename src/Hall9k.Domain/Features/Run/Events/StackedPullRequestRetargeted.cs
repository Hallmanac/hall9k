namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A stacked child's pull request was retargeted on GitHub, from the parent's branch onto the
/// project's own base branch, because closeout observed the parent's pull request merge (task: a
/// stacked pull-request edge exists as an explicit opt-in dependency). Recorded on the CHILD's run
/// stream, by whichever node observed the parent's merge.
/// <para>
/// Informational, like <see cref="PullRequestMechanicalRebaseAttempted"/>: it never moves
/// <see cref="RunState"/> or <see cref="ReviewPhase"/> on its own. Ordinarily a stacked replay
/// dispatches alongside it, and that is what actually moves this run on, arriving as the ordinary
/// <c>TaskReopened</c> + <see cref="RunSuperseded"/> pair every automatic follow-up uses — except
/// for a child that already sits on the base branch's own tip when its parent merges
/// (<c>StackedParentVerdict.ParentMergedAligned</c>), where this event is the whole of what
/// happens: there is nothing left to replay, so no follow-up run is dispatched alongside it.
/// </para>
/// <para>
/// Appended for a <em>failed</em> retarget too, with <paramref name="Succeeded"/> false: the whole
/// point of the record is that this child's pull request was, or was not, actually moved off a
/// branch that no longer exists, and a silent failure would leave a pull request aimed at a deleted
/// base with nothing on the stream to say why (AGENTS.md: the unobserved is admitted, never
/// guessed). A failed attempt dispatches no replay — the next sweep tries the retarget again.
/// </para>
/// </summary>
/// <param name="FromBase">
/// The base the pull request carried before this attempt — the parent's branch. Read off the run's
/// own recorded base rather than the provider, which is the same value in every case but one: a
/// child whose parent merged while it was still building has its pull request opened against the
/// project's base directly, because the parent's branch was already deleted
/// (<c>PullRequestOpener.ResolveOpenBaseAsync</c>), and the retarget that follows still names the
/// parent's branch here — the base this RUN recorded, which is the base that is moving, and not a
/// claim about what GitHub held.
/// </param>
/// <param name="ToBase">The base it was retargeted onto: the project's own base branch.</param>
/// <param name="BoundaryCommit">
/// The commit everything at or before which belongs to the parent, as this sweep observed it —
/// ordinarily the <c>&lt;upstream&gt;</c> the replay drops the parent's commits at, and for
/// <c>StackedParentVerdict.ParentMergedAligned</c> the project's own base branch tip this pull
/// request already sits on instead, with no replay behind it. <c>StackedParentWatch</c>'s own doc
/// has the full account of which observed commit this can be — the parent branch's own head, the
/// child's recorded fork point, the base branch's own tip, or a merge-base of the child and that
/// tip once a merged parent's boundary is confirmed already on its line. Named for the role it
/// plays rather than for one of those, because a reader following the field has to know what it
/// means when the parent DID advance past the child's cut point before merging (independent pre-PR
/// review, cycle 1, conformance lens). Empty when no boundary could be observed, which is why the
/// replay refuses to dispatch rather than guessing one.
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
