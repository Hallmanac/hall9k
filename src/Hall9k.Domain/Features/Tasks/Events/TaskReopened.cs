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
    string? PullRequestHeadSha = null);
