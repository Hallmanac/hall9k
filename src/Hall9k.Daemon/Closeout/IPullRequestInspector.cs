namespace Hall9k.Daemon.Closeout;

/// <summary>
/// What kind of account the provider says an author is. Unpersisted in-process vocabulary
/// (TASK-MODEL.md §8 allows an enum for exactly that), read from GraphQL's own actor
/// <c>__typename</c> rather than guessed from the login.
/// <para>
/// Mannequin earns its own case because it is neither of the other two: it is the
/// placeholder GitHub creates for an identity an import never mapped to a real account.
/// Nobody is behind one, so it must not inflate the human thread count, and the review-request
/// endpoint rejects it, so it must never be asked for a review either.
/// </para>
/// </summary>
public enum ReviewerKind
{
    /// <summary>A person: User, and every other actor type that is not a bot or a mannequin.</summary>
    Human,

    /// <summary>An app account (GraphQL Bot), including the Copilot reviewer.</summary>
    Bot,

    /// <summary>An unclaimed placeholder identity — nobody to ask, and nobody waiting.</summary>
    Mannequin,
}

/// <summary>
/// One account that authored something on a pull request, as the provider reported it. The
/// kind is read from the provider's own actor type rather than guessed from the login,
/// because the two forms of the same account differ by API surface: GraphQL reports the bare
/// login (copilot-pull-request-reviewer) and the REST review-request endpoint addresses app
/// accounts by the [bot]-suffixed form, while a human login must never be suffixed at all.
/// <para>
/// LastReviewedCommit is the commit this account's latest review was left on, and is set only
/// where the account is read as a reviewer — a thread's author carries null, and so does a
/// review the provider reported without a commit. It is what the countersign compares against
/// the pull request's head: a reviewer whose latest review already covers the current head has
/// nothing left to be asked about (Decisions Log #62).
/// </para>
/// </summary>
public sealed record PullRequestReviewer(string Login, ReviewerKind Kind, string? LastReviewedCommit = null)
{
    /// <summary>An app account: the [bot] suffix decision downstream rests on this.</summary>
    public bool IsBot => Kind == ReviewerKind.Bot;

    /// <summary>A person, who may be waiting on an answer — the care asymmetry keys off this.</summary>
    public bool IsHuman => Kind == ReviewerKind.Human;

    /// <summary>Whether a review request addressed here can be accepted at all.</summary>
    public bool IsRequestable => Kind is ReviewerKind.Human or ReviewerKind.Bot;
}

/// <summary>
/// An error-placeholder review: the reviewer's LATEST review says it could not review
/// the pull request. Reviewer is the provider login the review was authored under;
/// Url identifies the exact errored review (the monitor's dedup key, and what a park
/// reason names for the human).
/// </summary>
public sealed record ErroredReview(string Reviewer, string Url);

/// <summary>
/// One poll's observation of a pull request, as reported by the provider. Timestamps are
/// the provider's own (null when unreported — never guessed). FailingChecks holds the
/// names of checks that completed and failed; HasPendingChecks means the CI picture is
/// still incomplete, so the monitor waits rather than acting on a partial failure list.
/// <para>
/// UnresolvedReviewThreadCount counts EVERY unresolved review thread, whoever started it
/// (Decisions Log #62): Copilot is one reviewer among many, and a teammate's unresolved
/// thread is feedback on exactly the same footing. UnresolvedHumanThreadCount is how many
/// of those a person started, which is what lets the follow-up's dispatch reason say whether
/// somebody is waiting on an answer — a bot's thread is not counted, and neither is a
/// mannequin's, since there is nobody behind one. Threads the pull request's own author
/// started count too, and count as human: agents author as the human but only ever REPLY
/// within threads, so a thread whose first comment is the author's own is a human's
/// self-note (the self-review discriminator — see the invariant in AGENTS.md).
/// UnresolvedReviewThreadIds and UnresolvedHumanThreadIds carry the same two counts as
/// exact id sets (Decisions Log #77, backlog 45): the full set is the closeout budget's
/// mechanical obstruction key ("the same unresolved findings" the backlog card names), and
/// the human subset is what a later poll diffs to recognize a newly opened human thread —
/// one of the two signals that grants a lap regardless of the progress cap.
/// </para>
/// <para>
/// Reviewers holds the accounts whose latest review is on this pull request, excluding its
/// author and any account the provider will not accept a review request for, which is who a
/// countersign re-request is addressed to. HeadCommit is the commit those reviews are
/// compared against — a reviewer whose latest review sits on the head has already seen what
/// the fixes pushed — and is null when the provider did not report one, in which case no
/// reviewer is assumed to be up to date. ErroredReview is set when a reviewer's latest
/// review is an error placeholder: an errored review produces zero threads, so without this
/// signal it would read as a clean pass (origin incident: PR #6, 2026-08-17, GitHub partial
/// outage).
/// </para>
/// <para>
/// PendingReviewRequestLogins is the other human-engagement signal (Decisions Log #77,
/// backlog 45): who currently has a pending review request. A login that was not pending as
/// of the task's last automatic decision but is now is a human re-requesting a review — the
/// platform's own re-requests are compared away separately (RunAggregate.RequestedReviewerLogins),
/// never inferred from this list alone. A candidate third signal, a new top-level pull-request
/// comment, was cut before merge: agents here post top-level comments too (answering a review
/// body with `gh pr comment`), authored under the same login as a human's, so the provider's
/// actor type cannot tell the two apart the way it can for a review thread's starter.
/// </para>
/// <para>
/// One silence this cannot see: GitHub hides a review's comments while the review is still
/// PENDING (unsubmitted). Feedback reaches the platform only when its author clicks Submit
/// review, so a reviewer typing comments into a draft is invisible here — correctly, since
/// nothing has been said yet, but worth knowing when a PR looks quiet.
/// </para>
/// </summary>
public sealed record PullRequestSnapshot(
    bool IsMerged,
    bool IsClosed,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<string> FailingChecks,
    bool HasPendingChecks,
    int UnresolvedReviewThreadCount,
    int UnresolvedHumanThreadCount,
    IReadOnlyList<PullRequestReviewer> Reviewers,
    ErroredReview? ErroredReview,
    string? HeadCommit = null,
    IReadOnlyList<string>? UnresolvedReviewThreadIds = null,
    IReadOnlyList<string>? UnresolvedHumanThreadIds = null,
    IReadOnlyList<string>? PendingReviewRequestLogins = null)
{
    /// <summary>Every unresolved thread's id, or empty when the provider read predates ids being collected.</summary>
    public IReadOnlyList<string> ThreadIds => UnresolvedReviewThreadIds ?? [];

    /// <summary>The human-started subset of <see cref="ThreadIds"/>.</summary>
    public IReadOnlyList<string> HumanThreadIds => UnresolvedHumanThreadIds ?? [];

    /// <summary>Reviewers with a pending review request right now.</summary>
    public IReadOnlyList<string> PendingReviewers => PendingReviewRequestLogins ?? [];
}

/// <summary>
/// A merge/close-only read: the same three state facts <see cref="PullRequestSnapshot"/>
/// carries, without the reviews, checks, or reviewer list a full inspection also gathers.
/// This is what the orphan sweep (Decisions Log #72) asks for — it dispatches no
/// follow-up onto a dead run, so it has no use for the second remote call a full
/// <see cref="IPullRequestInspector.InspectAsync"/> spends on an open pull request's
/// reviews (TASK-MODEL.md §2.2's "one gh read" per orphan candidate).
/// </summary>
public sealed record PullRequestStateSnapshot(
    bool IsMerged, bool IsClosed, DateTimeOffset? MergedAt, DateTimeOffset? ClosedAt);

/// <summary>
/// The closeout monitor's seam onto the PR provider (gh in production, a fake in
/// tests): the read side per poll, plus the one write closeout needs — re-requesting a
/// review. Both run against the project's shared repository path — the run's worktree may
/// already be gone, the origin remote is what matters.
/// </summary>
public interface IPullRequestInspector
{
    Task<PullRequestSnapshot> InspectAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken);

    /// <summary>
    /// The lean read behind <see cref="PullRequestStateSnapshot"/> — merge/close facts
    /// only, one remote call regardless of whether the pull request is still open. The
    /// orphan sweep is the only caller: it watches for a merge or a close and nothing
    /// else, so it never needs the reviews and checks <see cref="InspectAsync"/> also
    /// gathers.
    /// </summary>
    Task<PullRequestStateSnapshot> InspectStateAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Re-request a review through the provider's API — never the website, which may be
    /// down when this matters (the origin incident's exact circumstance). Two callers, two
    /// reasons: a reviewer whose review errored never reviewed at all, and a reviewer whose
    /// findings a fix follow-up has now pushed answers to is being asked to countersign
    /// them (Decisions Log #62).
    /// </summary>
    Task RerequestReviewAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
        CancellationToken cancellationToken);
}
