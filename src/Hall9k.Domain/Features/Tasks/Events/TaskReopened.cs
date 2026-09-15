using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A done task returns to the queue for a follow-up run on its existing pull-request
/// branch (PR closeout, Decisions Log #18/#20). Branch comes from the completing run's
/// record — it lives nowhere else on the Task stream; the pull-request URL already does
/// (TaskCompleted) and is not repeated here. Kind selects the follow-up prompt (null on
/// events recorded before the vocabulary existed reads as Unknown); Automatic marks
/// reopens driven by the closeout monitor rather than a human — the lifetime-ceiling
/// counter counts only these, and a human-initiated reopen resets it (Decisions Log #22).
/// </summary>
/// <param name="ObstructionKey">
/// This lap's obstruction identity, mechanically recorded (Decisions Log #80, backlog 45):
/// the failing check name, or the exact set of unresolved review-thread ids, at the moment
/// of dispatch. Null on a manual reopen (no obstruction to compare against — the human
/// grant wipes the progress counter regardless) and on events recorded before this
/// vocabulary existed.
/// </param>
/// <param name="ObstructionSummary">
/// A short, human-readable description of this lap's obstruction — what a park message
/// reads back as the lap history once the lifetime ceiling is reached. Null wherever
/// ObstructionKey is.
/// </param>
/// <param name="KnownHumanReviewThreadIds">
/// Human-started unresolved review-thread ids observed at this dispatch, the comparison
/// point the next automatic decision diffs against to recognize a newly opened human
/// thread (a human-grant signal, Decisions Log #80). Null/empty on a manual reopen — the
/// slate is wiped rather than carried forward.
/// </param>
/// <param name="HumanReviewThreads">
/// The same human-started unresolved threads <see cref="KnownHumanReviewThreadIds"/> names, with
/// the two facts an id alone cannot carry: who opened each one, and the link to it (task: a
/// review-feedback follow-up never answers a human reviewer in the owner's name on its own).
/// Both are read off the provider on the same sweep, so a park that asks the operator to approve
/// a reply can show them the thread rather than an opaque <c>PRRT_…</c>, and so the posting path
/// can tell a person's thread from a bot's without trusting a session's own say-so.
/// <para>
/// Beside the id list rather than replacing it: that list is the mechanical human-engagement
/// diff (Decisions Log #80) and every reader of it predates this field. Null on events recorded
/// before this field existed, which reads as "no thread detail observed" — the posting path then
/// has no provider-observed human thread to refuse on, exactly as it had none before this
/// existed.
/// </para>
/// <para>
/// Unlike <see cref="KnownHumanReviewThreadIds"/>, a MANUAL reopen carries this forward rather
/// than wiping it: that list is the progress-diff comparison point a human grant deliberately
/// resets, and this one is the posting path's whose-thread evidence, which a fresh grant has no
/// reason to blind. <c>h9k pr resolve</c> therefore hands back the aggregate's own
/// <c>KnownHumanReviewThreads</c>, since it makes no provider read of its own and the reopen
/// REPLACES whatever the last dispatch recorded — see that command for why carrying an
/// already-resolved thread forward only ever makes the refusal wider, never narrower.
/// </para>
/// </param>
/// <param name="KnownPendingReviewRequestLogins">Reviewers with a pending review request observed at this dispatch — the comparison point for detecting a human's own re-request.</param>
/// <param name="PullRequestHeadSha">
/// The pull request head closeout observed at the moment it decided to reopen this task
/// (task: a lap reviews only what it changed) — the seed for the follow-up run's opening
/// Discovery cycle diff instruction. Set only for an automatic ReviewFeedback or FailingChecks
/// reopen, where closeout's own remote inspection just read it; null for a Rebase reopen
/// (excluded, unchanged — Brian's 2026-09-04 triage ruling governs that path), a manual
/// h9k pr resolve reopen (no live pull-request inspection there to observe a head from), and
/// events recorded before this field existed.
/// </param>
/// <param name="StackReplayUpstreamCommit">
/// For a <see cref="FollowUpKind.StackReplay"/> reopen only (task: a stacked pull-request edge
/// exists as an explicit opt-in dependency): the commit this stacked child's branch was last built
/// on top of — the parent branch head it was cut from, or the base a previous replay put it on.
/// It is the <c>&lt;upstream&gt;</c> argument of the replay's own
/// <c>git rebase --onto &lt;new base&gt; &lt;upstream&gt;</c>, which is what drops the parent's
/// commits from the child's branch instead of replaying them a second time. Null on every other
/// reopen kind and on events recorded before this field existed — a replay is the only follow-up
/// that has an upstream to name.
/// </param>
/// <param name="StackReplayOntoCommit">
/// The replay's other half, and set on the same reopens: the commit it lands on — the parent's
/// freshly observed head for a force-push, or the base branch's own tip once the parent merged.
/// <para>
/// A commit rather than the ref (<c>origin/&lt;base&gt;</c>) deliberately, for two reasons that
/// point the same way. It makes the replay deterministic: the session lands exactly where closeout
/// looked, not on whatever the ref has become minutes later, which is the difference between a
/// mechanical operation and one whose result nobody observed — and this replay is the one follow-up
/// no reviewer ever reads. And it is what lets the run record its own new fork point
/// (<c>RunDispatched.BaseCommit</c>) as a fact rather than a prediction, which the NEXT replay
/// needs as its upstream. A base branch that moves during the replay is then the ordinary
/// freshness machinery's business, exactly as it is for every other run.
/// </para>
/// </param>
/// <param name="ChangesRequestedReviews">
/// For a <see cref="FollowUpKind.ReviewRequestedChanges"/> reopen only (task: a changes-requested
/// pull-request review from a human becomes a fix lap): every human CHANGES_REQUESTED review
/// closeout observed on the head, with the review body and every inline comment as findings. The
/// launcher renders them into the fix session's prompt in the shape platform review findings
/// take, so the session reads what the reviewer wrote rather than a count of it. Null on every
/// other reopen kind and on events recorded before this field existed.
/// </param>
/// <param name="ChecksPendingSince">
/// When the provider's CI picture was first observed still incomplete on the pull request this
/// follow-up was dispatched for, or null when it was complete at dispatch (and on events recorded
/// before this field existed — unknown, never a claimed "the checks were done"). A lap is now
/// dispatched for review feedback whatever the checks are doing (Decisions Log
/// #164), so a queued or claimed follow-up is routinely the right answer for a
/// pull request whose check is still pending; this is what lets the phase line for that row say how
/// long it has been pending instead of leaving the reader to guess. The reopen carries it because
/// nothing else on the queued row can: <c>Apply(TaskReopened)</c> clears
/// <c>TaskListItem.CurrentRunId</c>, so the run that made the observation is unreachable from the
/// row by the time it renders.
/// </param>
public sealed record TaskReopened(
    Guid Id,
    Guid PreviousRunId,
    string Branch,
    string? Reason,
    DateTimeOffset ReopenedAt,
    Guid ReopenedByOwnerId,
    FollowUpKind? Kind = null,
    bool Automatic = false,
    string? ObstructionKey = null,
    string? ObstructionSummary = null,
    IReadOnlyList<string>? KnownHumanReviewThreadIds = null,
    IReadOnlyList<string>? KnownPendingReviewRequestLogins = null,
    string? PullRequestHeadSha = null,
    string? StackReplayUpstreamCommit = null,
    string? StackReplayOntoCommit = null,
    IReadOnlyList<ChangesRequestedReview>? ChangesRequestedReviews = null,
    DateTimeOffset? ChecksPendingSince = null,
    IReadOnlyList<ReviewThreadReference>? HumanReviewThreads = null);
