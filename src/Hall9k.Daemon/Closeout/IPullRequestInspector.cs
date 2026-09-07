using Hall9k.Domain.Features.Run;

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
/// <para>
/// StandingReviewState is GitHub's own verdict word for this account's most recent VERDICT —
/// <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>, or the <c>DISMISSED</c> that retired one — with
/// StandingReviewCommit the commit that verdict was left on, and both null where no verdict was
/// observed at all. Read by the after-human-review merge gate (task: the people a pull request is
/// waiting on are named, and pre-approval gains a mode that waits for human review), which needs
/// "did this person actually approve" rather than the pull request's aggregate
/// <c>reviewDecision</c>: that verdict is null wherever no branch rule requires one, so a reviewer
/// who was asked and only commented would otherwise read as satisfied.
/// </para>
/// <para>
/// Standing is deliberately not the same fact as latest, and the pair of commit fields is what
/// makes the difference visible: a reviewer who approves the head and then answers a question with
/// a comment-only review has that comment as their LATEST review while GitHub keeps their approval
/// STANDING (it goes on reporting <c>reviewDecision: APPROVED</c>). Reading the verdict off the
/// latest review held such a merge forever and named the approver as the person it was waiting on
/// (independent pre-PR review, cycle 1, adversarial finding), so the verdict comes from a
/// verdict-only read (<c>GitHubPullRequestInspector.ReadStandingVerdicts</c>) while
/// <see cref="LastReviewedCommit"/> keeps answering the countersign's own, different question.
/// </para>
/// </summary>
public sealed record PullRequestReviewer(
    string Login,
    ReviewerKind Kind,
    string? LastReviewedCommit = null,
    string? StandingReviewState = null,
    string? StandingReviewCommit = null)
{
    /// <summary>An app account: the [bot] suffix decision downstream rests on this.</summary>
    public bool IsBot => Kind == ReviewerKind.Bot;

    /// <summary>A person, who may be waiting on an answer — the care asymmetry keys off this.</summary>
    public bool IsHuman => Kind == ReviewerKind.Human;

    /// <summary>Whether a review request addressed here can be accepted at all.</summary>
    public bool IsRequestable => Kind is ReviewerKind.Human or ReviewerKind.Bot;

    /// <summary>
    /// Whether this account's STANDING verdict requests changes — whose verdict a
    /// CHANGES_REQUESTED decision belongs to. Standing rather than latest, so a reviewer who
    /// requests changes and then comments is still named as the author of the verdict GitHub is
    /// still reporting.
    /// </summary>
    public bool RequestedChanges => StandingReviewState == "CHANGES_REQUESTED";

    /// <summary>
    /// Whether this account's standing verdict approves <paramref name="headCommit"/>
    /// specifically. A null head, or a verdict the provider reported without a commit, reads as
    /// NOT approved: unobservable is not agreement, the same reading
    /// <see cref="LastReviewedCommit"/> already gets from the countersign.
    /// </summary>
    public bool HasApproved(string? headCommit) =>
        StandingReviewState == "APPROVED"
        && headCommit is not null
        && string.Equals(StandingReviewCommit, headCommit, StringComparison.Ordinal);
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
/// exact id sets (Decisions Log #80, backlog 45): the full set is the closeout budget's
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
/// PendingReviewRequestLogins is the other human-engagement signal (Decisions Log #80,
/// backlog 45): who currently has a pending review request. A login that was not pending as
/// of the task's last automatic decision but is now is a human re-requesting a review — the
/// platform's own re-requests are compared away separately (RunDetails.RequestedReviewerLogins),
/// never inferred from this list alone. Bot-typed requests, and the known Copilot logins for
/// the cases the unified Copilot app has surfaced under User instead, are excluded before this
/// list is built: GitHub's own "review new commits automatically" setting recreates Copilot's
/// pending request on every push, which is not a human acting and would otherwise let a
/// follow-up's own push manufacture the signal that grants its next lap (adversarial pre-PR
/// review, 2026-08-24). A candidate third signal, a new top-level pull-request comment, was cut
/// before merge: agents here post top-level comments too (answering a review body with
/// `gh pr comment`), authored under the same login as a human's, so the provider's actor type
/// cannot tell the two apart the way it can for a review thread's starter or a review request.
/// </para>
/// <para>
/// ChangesRequestedReviews is the narrower, sharper read alongside the thread counts above: a
/// PERSON's latest review on this exact head formally requesting changes, body and inline
/// comments and all (task: a changes-requested pull-request review from a human becomes a fix
/// lap). A bot's changes-requested review is deliberately absent from it — Copilot's findings stay
/// on the automated thread path, which argues and resolves on its own, because what earns a
/// separate lap here is that a disagreement with a person must never be sent by an agent.
/// </para>
/// <para>
/// One silence this cannot see: GitHub hides a review's comments while the review is still
/// PENDING (unsubmitted). Feedback reaches the platform only when its author clicks Submit
/// review, so a reviewer typing comments into a draft is invisible here — correctly, since
/// nothing has been said yet, but worth knowing when a PR looks quiet.
/// </para>
/// <para>
/// IsConflicting is GitHub's own <c>mergeable == CONFLICTING</c> read (backlog 44), observed
/// alongside the reviews call rather than inferred from how long the branch has sat open —
/// the never-guess rule applies to staleness exactly as it does to everything else this
/// snapshot carries. Read only while the pull request is open, the same scope every other
/// review field here has; a merely-behind-but-mergeable branch is deliberately not this
/// field's concern (GitHub merges those fine on its own).
/// </para>
/// <para>
/// CopilotReviewState is the post-PR review watcher's own read (origin: PR #50 sat Delivered
/// for 23 minutes with a landed Copilot review nobody had read before the merge): whether
/// Copilot's review has landed (a real, non-errored review is on the pull request),
/// is requested but not yet submitted, or neither. It answers a narrower question than
/// Reviewers/ErroredReview above — those exist for the countersign and the errored-review
/// re-request, this exists only so the Delivered phase line can say which of the three it is
/// watching for. CopilotReviewThreadCount is every thread the currently-landed review itself
/// opened, resolved or not — not every thread Copilot has ever opened across the pull request's
/// history, since a superseded review's threads are not what "landed" now names alongside itself
/// (Decisions Log #89).
/// </para>
/// <para>
/// BaseRefName is the pull request's actual base branch name, as GitHub reports it — never
/// assumed to be the project's own configured base branch. A human can retarget a pull request's
/// base on GitHub itself, and this platform now retargets one itself when a stacked child's parent
/// merges (Decisions Log #144). Either way, GitHub's own <see cref="IsConflicting"/> read is
/// against that retargeted base,
/// not against <c>project.BaseBranch</c>: the closeout engine's mechanical rebase fast path reads
/// this field before ever fetching or rebasing, specifically so it never force-pushes a rebase
/// onto the wrong base (independent pre-PR review, cycle 1, adversarial lens). Null when the
/// provider read predates this field being collected — treated as "unknown, proceed as before"
/// rather than as a mismatch.
/// </para>
/// <para>
/// HasObservedChecks is whether GitHub's own <c>statusCheckRollup</c> actually reported any
/// check at all, as distinct from <see cref="HasPendingChecks"/> and <see cref="FailingChecks"/>
/// both reading as clean. The two are not the same fact: a rollup GitHub has not yet populated
/// (a workflow run object typically takes only seconds to appear, longer under Actions queue
/// congestion, or right after a mechanical rebase re-triggers CI) comes back as an empty array
/// indistinguishable from a repository with no CI configured at all — both leave
/// <see cref="HasPendingChecks"/> false and <see cref="FailingChecks"/> empty. Only the pre-approved
/// merge gate reads this field (task: a task can be published pre-approved, independent pre-PR
/// review, cycle 1, adversarial lens): "no check observed" is not the same claim as "CI green",
/// and treating an unregistered rollup as green would let a pre-approved task merge before its
/// own CI had a chance to run. Defaults true so a snapshot built before this field existed, or a
/// test fixture that never sets it, reads as the ordinary "nothing here to wait on" case.
/// </para>
/// <para>
/// ReviewThreadsTruncated is GitHub's own <c>reviewThreads.pageInfo.hasNextPage</c> read: whether
/// the provider's own 100-thread page cap (a deliberate cap on <c>GitHubPullRequestInspector.ReviewsQuery</c>,
/// not missing pagination) actually cut off real threads this snapshot never saw.
/// <see cref="UnresolvedReviewThreadCount"/> reading zero is not the same claim as "every thread is
/// resolved" when this is true — only that the first 100 happened to be. The ordinary follow-up
/// path already tolerates the cap safely (a monster PR simply waits for a human to look at it
/// eventually, since nothing there merges on its own), but the pre-approved merge gate has no
/// human left in that loop, so it treats a truncated read as an obstruction of its own rather than
/// trusting the zero (independent pre-PR review, cycle 1, adversarial finding).
/// </para>
/// <para>
/// RequestedHumanReviewerLogins is who this pull request's review-request timeline shows was asked
/// for a review and not un-asked again, humans and teams (task: the people a pull request is
/// waiting on are named, and pre-approval gains a mode that waits for human review). Distinct from
/// <see cref="OutstandingReviewerLogins"/>, which is only who is asked <em>right now</em>: a
/// reviewer who was asked and has since answered is gone from that list and still in this one,
/// which is the whole point — the after-human-review merge gate has to know a human was brought
/// into the loop at all before it can ask whether they approved. Null on a snapshot built before
/// this was collected, and on a test fixture that never sets it, which reads as "no request
/// observed" and therefore holds that gate rather than opening it.
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
    ExternalReviewState CopilotReviewState,
    int CopilotReviewThreadCount,
    string? HeadCommit = null,
    IReadOnlyList<string>? UnresolvedReviewThreadIds = null,
    IReadOnlyList<string>? UnresolvedHumanThreadIds = null,
    IReadOnlyList<string>? PendingReviewRequestLogins = null,
    bool IsConflicting = false,
    string? BaseRefName = null,
    string? ReviewDecision = null,
    IReadOnlyList<string>? OutstandingReviewerLogins = null,
    bool HasObservedChecks = true,
    bool ReviewThreadsTruncated = false,
    IReadOnlyList<string>? RequestedHumanReviewerLogins = null,
    IReadOnlyList<ChangesRequestedReview>? ChangesRequestedReviews = null)
{
    /// <summary>
    /// How a requested TEAM reviewer is recorded in every reviewer list here, since GitHub exposes
    /// a team by slug and no login: <c>team:&lt;slug&gt;</c>. One home for the prefix because three
    /// places depend on it agreeing — the two provider readers that write it
    /// (<c>GitHubPullRequestInspector.ReadOutstandingReviewerLogins</c> and
    /// <c>ReadRequestedHumanReviewerLogins</c>) and <see cref="HumanReviewersAwaitingApproval"/>,
    /// which recognizes a team by it in order to ask a different approval question of it than of a
    /// person.
    /// </summary>
    public const string TeamReviewerPrefix = "team:";

    /// <summary>
    /// Every human reviewer's CHANGES_REQUESTED review sitting on this head, with the review body
    /// and each inline comment as findings (task: a changes-requested pull-request review from a
    /// human becomes a fix lap). Empty when there is none, and empty on a provider read that
    /// predates this field being collected — which reads as "none observed", the same conservative
    /// default every other field here takes, and leaves such a pull request on the thread-based
    /// <c>FollowUpKind.ReviewFeedback</c> path exactly as before.
    /// </summary>
    public IReadOnlyList<ChangesRequestedReview> ChangesRequested => ChangesRequestedReviews ?? [];

    /// <summary>Every unresolved thread's id, or empty when the provider read predates ids being collected.</summary>
    public IReadOnlyList<string> ThreadIds => UnresolvedReviewThreadIds ?? [];

    /// <summary>The human-started subset of <see cref="ThreadIds"/>.</summary>
    public IReadOnlyList<string> HumanThreadIds => UnresolvedHumanThreadIds ?? [];

    /// <summary>Reviewers with a pending review request right now.</summary>
    public IReadOnlyList<string> PendingReviewers => PendingReviewRequestLogins ?? [];

    /// <summary>Every requested reviewer, Copilot included and unfiltered — see <see cref="Events.ExternalReviewObserved.OutstandingReviewerLogins"/>'s own doc.</summary>
    public IReadOnlyList<string> OutstandingReviewers => OutstandingReviewerLogins ?? [];

    /// <summary>
    /// Every requested reviewer OTHER than Copilot — a pre-approved merge gate's own "no
    /// outstanding requested reviewer" check (task: a task can be published pre-approved):
    /// Copilot outstanding is handled separately, through <see cref="ExternalReviewState"/> and its
    /// own bounded settle window, not folded into this list. Recorded on
    /// <see cref="Events.ExternalReviewObserved.OutstandingHumanReviewerLogins"/> too, so the CLI's
    /// display reads the identical filtered list rather than duplicating the classification.
    /// </summary>
    public IReadOnlyList<string> OutstandingHumanReviewers => [.. OutstandingReviewers.Where(
        login => !GitHubPullRequestInspector.IsCopilotLogin(login))];

    /// <summary>
    /// Whether any requested reviewer other than Copilot is still outstanding — a pre-approved
    /// merge gate's own "no outstanding requested reviewer" check (task: a task can be published
    /// pre-approved).
    /// </summary>
    public bool HasOutstandingHumanReviewer => OutstandingHumanReviewers.Count > 0;

    /// <summary>
    /// Whether GitHub's own review-decision verdict (<see cref="ReviewDecision"/>) is satisfied —
    /// null (no branch rule requires one) or <c>APPROVED</c>. <c>REVIEW_REQUIRED</c> and
    /// <c>CHANGES_REQUESTED</c> both read as unsatisfied: either way a human's own approval is
    /// still outstanding, which is exactly the "waiting on human approval" visible state (task: a
    /// task can be published pre-approved).
    /// </summary>
    public bool ReviewDecisionSatisfied => ReviewDecision is null or "APPROVED";

    /// <summary>
    /// Every human (or team) reviewer this pull request has had a review request for and has not
    /// had it withdrawn again — the union of the provider's own review-request timeline, net of
    /// removals, with whoever is outstanding right now (task: the people a pull request is waiting
    /// on are named, and pre-approval gains a mode that waits for human review).
    /// <para>
    /// Netting removals matters because a request GitHub retires by itself, when its reviewer
    /// submits a review, leaves no removal event — so a reviewer who answered stays in this set
    /// and their verdict is checked, while one whose request a human took back drops out, and the
    /// after-human-review gate goes back to saying no reviewer has been asked rather than waiting
    /// forever on somebody nobody is asking any more.
    /// </para>
    /// <para>
    /// The union with <see cref="OutstandingHumanReviewers"/> is the belt to the timeline's braces:
    /// the timeline read is capped (a deliberate cap, like every other read here), and a request
    /// that is outstanding right now was definitionally made at some point, whether or not the
    /// event that made it still fits inside the cap.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> HumanReviewersEverRequested =>
    [
        .. (RequestedHumanReviewerLogins ?? [])
            .Concat(OutstandingHumanReviewers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Whether a human review has ever been asked for on this pull request — the first of the two
    /// gates <c>PreApprovalMode.AfterHumanReview</c> adds. False is a wait, never a park: the owner
    /// adds a reviewer on GitHub, or flips the mode to on.
    /// </summary>
    public bool HasEverRequestedHumanReviewer => HumanReviewersEverRequested.Count > 0;

    /// <summary>
    /// The ever-requested human reviewers whose standing verdict is not an approval of
    /// <see cref="HeadCommit"/> — the second of the two gates
    /// <c>PreApprovalMode.AfterHumanReview</c> adds, and the list the display names as who the
    /// merge is waiting on. A reviewer who commented without ever approving, one who requested
    /// changes, one whose approval was dismissed, one whose approval sits on a superseded commit,
    /// and one who has not answered at all are all here: each is a person who was asked and has
    /// not approved what is about to merge. A reviewer who approved the head and then left a
    /// comment-only review is NOT here, because their approval stands — see
    /// <see cref="PullRequestReviewer.StandingReviewState"/>.
    /// <para>
    /// A requested TEAM (recorded <c>team:&lt;slug&gt;</c> by
    /// <c>GitHubPullRequestInspector.ReadOutstandingReviewerLogins</c>) is not asked to approve
    /// under its own slug — GitHub satisfies a team's request when any one member reviews, and it
    /// is that member's own login, never the slug, that carries the review, so demanding an
    /// approval FROM the slug would hold this gate shut forever. What answers for it instead is
    /// <see cref="HasStandingHumanApprovalOfHead"/>: while nobody at all has a standing approval of
    /// the head, every requested team stays on this list and the merge keeps waiting. That is the
    /// weakest requirement this read can actually observe — team membership is not in the pull
    /// request payload, so which accounts belong to the slug is genuinely unknown here and is not
    /// guessed at (AGENTS.md, never guess at unobserved facts) — and it is what keeps the mode's
    /// promise: a team request answered by a member's comment-only or changes-requested review
    /// retires the pending request, and without this the gate would open on a pull request no
    /// person ever approved, which is exactly what an individually requested reviewer's identical
    /// answer holds (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// <para>
    /// This is strictly narrower than the outstanding-request refusal that runs ahead of it
    /// (<see cref="HasOutstandingHumanReviewer"/>, which every automatic merge checks first): that
    /// one covers a team nobody has answered for yet, this one covers a team somebody answered
    /// without approving. Like every other wait this mode adds, it has no clock and never parks —
    /// the owner's levers are an approval on GitHub, another reviewer, or flipping the mode to on.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> HumanReviewersAwaitingApproval =>
    [
        .. HumanReviewersEverRequested
            .Where(login => login.StartsWith(TeamReviewerPrefix, StringComparison.Ordinal)
                ? !HasStandingHumanApprovalOfHead
                : !Reviewers.Any(reviewer =>
                    string.Equals(reviewer.Login, login, StringComparison.OrdinalIgnoreCase)
                    && reviewer.HasApproved(HeadCommit))),
    ];

    /// <summary>
    /// Whether anybody other than Copilot has a standing approval of <see cref="HeadCommit"/> —
    /// what a requested TEAM's approval requirement is answered by, since a slug carries no review
    /// of its own (see <see cref="HumanReviewersAwaitingApproval"/>).
    /// <para>
    /// "Anybody" is deliberate: the approving account need not be the one that was requested,
    /// because a team request is satisfied by whichever member picks it up and this read cannot
    /// see who that team's members are. Copilot is filtered on the same
    /// <c>GitHubPullRequestInspector.IsCopilotLogin</c> table every other list here uses, so the
    /// review it leaves — automatic on every push, wherever the repository turns that setting on —
    /// can never stand in for the human review this mode waits for; everyone else counts, the same
    /// "Copilot out, everyone else in" reading <see cref="HumanChangesRequestedBy"/> gives its own
    /// blocking verdicts.
    /// </para>
    /// </summary>
    public bool HasStandingHumanApprovalOfHead => Reviewers.Any(reviewer =>
        !GitHubPullRequestInspector.IsCopilotLogin(reviewer.Login) && reviewer.HasApproved(HeadCommit));

    /// <summary>
    /// Whose standing verdict requests changes — who to name alongside a <c>CHANGES_REQUESTED</c>
    /// review decision, which is a verdict about the pull request and says nothing on its own about
    /// whose verdict it is.
    /// <para>
    /// Filtered on the same rule as <see cref="OutstandingHumanReviewers"/> and
    /// <see cref="HumanReviewersEverRequested"/> — Copilot out, everyone else in — rather than on
    /// <see cref="PullRequestReviewer.IsHuman"/>, so all three lists that reach the display mean the
    /// identical thing by "human". Copilot's own read stays where it has always been
    /// (<see cref="CopilotReviewState"/> and its bounded settle window); any other account's
    /// changes-requested verdict genuinely blocks the merge and is named rather than silently
    /// dropped, which would leave a reader with an unsatisfied review decision and nobody attached
    /// to it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> HumanChangesRequestedBy =>
    [
        .. Reviewers
            .Where(reviewer => reviewer.RequestedChanges
                && !GitHubPullRequestInspector.IsCopilotLogin(reviewer.Login))
            .Select(reviewer => reviewer.Login)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];
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

    /// <summary>
    /// Rebase-merges a pre-approved task's pull request (task: a task can be published
    /// pre-approved, design ruling 8: rebase merge, linear history — never an agent). Throws on any
    /// failure, mechanical or otherwise, the same convention <see cref="RerequestReviewAsync"/>'s
    /// own implementation already uses for a refused call — the caller treats a thrown exception as
    /// one unit spent against the pre-approved task's own mechanical-resolution budget, never as a
    /// park-worthy human waypoint by itself. <paramref name="expectedHeadCommit"/>, when known, is
    /// passed to the provider's own head-commit match so a merge never lands a commit this sweep
    /// never actually inspected — the fourth gate ("head must postdate the last fix session's
    /// completion") enforced by the provider itself rather than trusted to this sweep's own timing.
    /// </summary>
    Task MergeAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a pull request's base branch — the one provider write a stacked child needs (task: a
    /// stacked pull-request edge exists as an explicit opt-in dependency), used once, when the
    /// child's parent merges and the child must be retargeted off the parent's now-gone branch onto
    /// <paramref name="baseBranch"/>. Throws on any failure, the same convention
    /// <see cref="MergeAsync"/> and <see cref="RerequestReviewAsync"/> already use; the caller
    /// records the failure and the next sweep tries again.
    /// <para>
    /// Idempotent by the provider's own behaviour: GitHub accepts a base a pull request is already
    /// set to. That is what makes a retry safe after a lost fence race, where the record of the
    /// first successful retarget rolls back but the retarget itself does not.
    /// </para>
    /// </summary>
    Task RetargetAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
        CancellationToken cancellationToken);
}
