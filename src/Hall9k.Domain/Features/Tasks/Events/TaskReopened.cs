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
    string? StackReplayOntoCommit = null);
