namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor's post-PR review-state watcher observed Copilot's review state
/// (<see cref="ExternalReviewState"/>: landed, requested but not yet submitted, absent, stale,
/// or unclassifiable — origin incident: PR #50 sat Delivered for 23 minutes with a landed
/// Copilot review nobody had read before the merge). Read only by the Delivered phase line —
/// it never moves <see cref="RunState"/> and never becomes a new task lifecycle status.
/// </summary>
/// <param name="ThreadCount">
/// Every review thread Copilot's review opened, resolved or not — what the phase names
/// alongside a landed review. Distinct from <see cref="ReviewFeedbackReceived"/>'s unresolved
/// count, which drives the run into ReviewPending regardless of who opened the thread.
/// </param>
/// <param name="ChecksPending">
/// The provider's own CI picture was still incomplete at the moment this was observed
/// (<c>PullRequestSnapshot.HasPendingChecks</c>), recorded in the same sweep and ahead of every
/// branch that acts on the snapshot — the CloseoutParked short-circuit included, not only the
/// checks-and-threads read (pre-PR review, cycle 3). While this is true a landed review has not
/// been read against a settled CI result and its threads have not been re-checked for new
/// unresolved ones this sweep either, so the Delivered surfaces must not name the human as the
/// last gate — the identical caveat a quiet pull request already carries. False means only that
/// the provider had a complete CI answer at that moment (pre-PR review, cycle 4) — it is not a
/// claim that this sweep went on to read past failing checks or unresolved threads, or that none
/// were found: a parked run records this and returns without ever reaching those reads.
/// </param>
/// <param name="ReviewDecision">
/// GitHub's own branch-protection-aware verdict for the pull request — <c>APPROVED</c>,
/// <c>REVIEW_REQUIRED</c>, <c>CHANGES_REQUESTED</c>, or null when the repository has no branch
/// rule requiring one (task: a task can be published pre-approved) — read purely for display: a
/// pre-approved task's own merge gate reads this fresh off the same sweep's live snapshot rather
/// than off this persisted copy, so this field exists only so the Delivered surfaces can say
/// "waiting on human approval" without a live GitHub call of their own.
/// </param>
/// <param name="OutstandingReviewerLogins">
/// Every login with a review request currently outstanding, Copilot included and unfiltered —
/// deliberately the raw provider read, unlike <c>PendingReviewRequestLogins</c>'s own
/// human-engagement filtering, because a pre-approved merge gate (and the display that explains
/// it) needs "is anyone still asked to look", not "did a human just re-ask".
/// </param>
/// <param name="OutstandingHumanReviewerLogins">
/// The subset of <paramref name="OutstandingReviewerLogins"/> that is not Copilot
/// (<c>PullRequestSnapshot.OutstandingHumanReviewers</c>), recorded so the CLI's own "waiting on
/// human approval" display can read a Copilot-filtered list without duplicating the
/// Copilot-detection table, which lives only in the daemon and is not reachable from
/// <c>Hall9k.Cli</c> (task: a task can be published pre-approved).
/// </param>
/// <param name="ChangesRequestedByLogins">
/// The human reviewers whose STANDING verdict on this pull request requests changes
/// (<c>PullRequestSnapshot.HumanChangesRequestedBy</c>) — who to name when
/// <paramref name="ReviewDecision"/> reads <c>CHANGES_REQUESTED</c>, since the decision itself is
/// a verdict about the pull request and says nothing about whose verdict it is (task: the people a
/// pull request is waiting on are named, and pre-approval gains a mode that waits for human
/// review). Null on an observation recorded before this was collected — an unrecorded list, never
/// a claimed empty one.
/// </param>
/// <param name="HumanReviewEverRequested">
/// Whether a human review has ever been requested on this pull request, as of this observation
/// (<c>PullRequestSnapshot.HasEverRequestedHumanReviewer</c>) — the fact
/// <c>PreApprovalMode.AfterHumanReview</c> holds the merge on, and what <c>h9k task show</c>
/// renders under that mode. Null on an observation recorded before this was collected: unknown,
/// not "no reviewer was ever asked".
/// </param>
/// <param name="HumanReviewersAwaitingApprovalLogins">
/// The ever-requested human reviewers whose standing verdict is not an approval of the current head
/// (<c>PullRequestSnapshot.HumanReviewersAwaitingApproval</c>) — exactly who
/// <c>PreApprovalMode.AfterHumanReview</c> is still waiting on, so the display names the same
/// people the daemon's own gate is holding for rather than deriving a second, possibly different
/// answer. An entry may be a requested TEAM, recorded <c>team:&lt;slug&gt;</c> exactly as
/// <paramref name="OutstandingReviewerLogins"/> records one, since GitHub exposes no login for a
/// team and inventing one would be a guess: the team is who was asked, and what clears it is a
/// standing approval of the head from anybody. Null on an observation recorded before this was
/// collected.
/// </param>
public sealed record ExternalReviewObserved(
    Guid Id,
    ExternalReviewState State,
    int ThreadCount,
    bool ChecksPending,
    DateTimeOffset ObservedAt,
    string? ReviewDecision = null,
    IReadOnlyList<string>? OutstandingReviewerLogins = null,
    IReadOnlyList<string>? OutstandingHumanReviewerLogins = null,
    IReadOnlyList<string>? ChangesRequestedByLogins = null,
    bool? HumanReviewEverRequested = null,
    IReadOnlyList<string>? HumanReviewersAwaitingApprovalLogins = null);
