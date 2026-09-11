using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run;

public sealed class RunAggregate
{
    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid NodeId { get; private set; }
    public Guid OwnerId { get; private set; }
    public int LeaseGeneration { get; private set; }
    public Guid SessionId { get; private set; }
    public string WorktreePath { get; private set; } = string.Empty;
    public string Branch { get; private set; } = string.Empty;
    /// <summary>
    /// Where this run's artifacts lived at dispatch, as recorded on <see cref="RunDispatched"/>.
    /// A task's directory can move across the <c>tasks</c>/<c>tasks/_archive</c> boundary and
    /// back after dispatch (backlog 51, PLAN.md §16 #84), so this is a dispatch-time record, not
    /// a live pointer — resolve it through <see cref="RunPaths.ResolveCurrentDirectory"/> before
    /// use rather than trusting it verbatim. A stream written before the field existed falls back
    /// to <see cref="RunPaths.GlobalDirectory"/> — the same place its files have always actually
    /// been.
    /// </summary>
    public string RunDirectory { get; private set; } = string.Empty;
    /// <summary>The pull request's base branch as read at dispatch, for a pr-review run only. See <see cref="Events.RunDispatched"/>'s own doc for why.</summary>
    public string? PrReviewBaseRefName { get; private set; }
    /// <summary>
    /// The branch this run's work sits on top of, as recorded on <see cref="RunDispatched"/> —
    /// blank for every run based on the project's own base branch, which is all of them but a
    /// stacked child's. Read it through <see cref="BaseBranchOr"/> rather than directly, so blank
    /// always resolves to the project's base rather than to an empty ref.
    /// </summary>
    public string BaseBranch { get; private set; } = string.Empty;

    /// <summary>See <see cref="Events.RunDispatched"/>'s own doc — this branch's fork point as observed, blank when nothing was.</summary>
    public string BaseCommit { get; private set; } = string.Empty;

    /// <summary>
    /// This run's base branch, resolving blank to <paramref name="projectBaseBranch"/>. Every read
    /// of <see cref="BaseBranch"/> goes through here: blank means "the project's own", so a caller
    /// that reads the field raw would hand an empty ref to git or to <c>gh pr create</c>.
    /// </summary>
    public string BaseBranchOr(string projectBaseBranch) =>
        BaseBranch.IsNotBlank() ? BaseBranch : projectBaseBranch;
    public ExecutorMode ExecutorMode { get; private set; } = ExecutorMode.Unknown;
    /// <summary>The model the build session was spawned on, as resolved at dispatch (log #33). Unknown on streams written before the chain existed.</summary>
    public AgentModel Model { get; private set; } = AgentModel.Unknown;
    /// <summary>
    /// Which pre-PR review stages this run gets, resolved once at dispatch and frozen for the
    /// run's whole lifetime (task: the review pipeline's stage composition becomes configuration
    /// recorded per run) — see this type's own doc for why this does not re-resolve live the way
    /// the review-cycle caps do. FullPipeline on every stream
    /// written before this field existed, byte-for-byte what those runs actually ran.
    /// </summary>
    public ReviewStageComposition ReviewStageComposition { get; private set; } = ReviewStageComposition.FullPipeline;
    public RunState State { get; private set; } = RunState.Unknown;
    public int? ProcessId { get; private set; }
    public DateTimeOffset? ProcessStartedAt { get; private set; }
    public string? PullRequestUrl { get; private set; }
    public int? PullRequestNumber { get; private set; }
    public long InputTokens { get; private set; }
    /// <summary>Input served from the prompt cache — priced apart from fresh input, so counted apart.</summary>
    public long CacheReadInputTokens { get; private set; }
    /// <summary>Input written into the prompt cache — priced apart from fresh input, so counted apart.</summary>
    public long CacheCreationInputTokens { get; private set; }
    /// <summary>Every input token the run was billed for, however the cache handled it.</summary>
    public long TotalInputTokens => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;
    public long OutputTokens { get; private set; }
    /// <summary>As reported by the agent result, never recomputed from the token counts.</summary>
    public decimal? CostUsd { get; private set; }
    public DateTimeOffset DispatchedAt { get; private set; }
    public bool IsFollowUp { get; private set; }
    /// <summary>See <see cref="Events.RunDispatched"/>'s own doc — the seed for this run's opening Discovery cycle's diff instruction.</summary>
    public string? OpeningReviewSinceSha { get; private set; }

    public DateTimeOffset? PullRequestMergedAt { get; private set; }
    public int UnresolvedReviewThreads { get; private set; }

    /// <summary>The last errored review observed — the monitor's dedup key: one re-request per errored review.</summary>
    public string? ErroredReviewUrl { get; private set; }

    /// <summary>
    /// The post-PR review watcher's latest read of Copilot's review state (Landed,
    /// RequestedPending, Stale, None, or Unknown) — read only by the Delivered phase line,
    /// never a lifecycle status and never a driver of <see cref="State"/>.
    /// </summary>
    public ExternalReviewState ExternalReviewState { get; private set; } = ExternalReviewState.Unknown;

    /// <summary>Every review thread Copilot's review opened, resolved or not, as of the last observation.</summary>
    public int ExternalReviewThreadCount { get; private set; }

    /// <summary>
    /// Whether the provider's CI picture was still incomplete as of the last observation
    /// (<see cref="Events.ExternalReviewObserved"/>). False means only that the provider had a
    /// complete CI answer at that moment — not that the sweep went on to read past failing
    /// checks or unresolved threads, or that none were found: a parked run records this and
    /// returns before ever reaching those reads.
    /// </summary>
    public bool ExternalReviewChecksPending { get; private set; }

    /// <summary>Whether closeout's mechanical rebase-before-reopen fast path last applied cleanly and pushed; null until one is attempted.</summary>
    public bool? LastMechanicalRebaseSucceeded { get; private set; }

    /// <summary>Whether the last stacked retarget attempt actually moved the pull request's base; null until one is tried. See <see cref="Events.StackedPullRequestRetargeted"/>.</summary>
    public bool? LastStackedRetargetSucceeded { get; private set; }
    /// <summary>What that retarget attempt did, or why it could not — the sentence h9k task show reads back.</summary>
    public string? LastStackedRetargetDetail { get; private set; }
    /// <summary>When the last stacked retarget was attempted; null until one is.</summary>
    public DateTimeOffset? LastStackedRetargetAt { get; private set; }
    /// <summary>What the last mechanical rebase attempt actually did or why it fell back — see <see cref="Events.PullRequestMechanicalRebaseAttempted"/>.</summary>
    public string? LastMechanicalRebaseDetail { get; private set; }

    /// <summary>The branch's new head after the last mechanical rebase's clean push; null on a fallback.</summary>
    public string? LastMechanicalRebasePushedCommit { get; private set; }

    /// <summary>When the last mechanical rebase attempt was made; null until one is.</summary>
    public DateTimeOffset? LastMechanicalRebaseAt { get; private set; }

    /// <summary>
    /// Whether the mandatory final full pass's own pre-flight rebase last applied — cleanly or
    /// through the recovery session — or found nothing to do (task: a run rebases its branch
    /// onto the current base branch); null until one is attempted. See
    /// <see cref="Events.RunRebasedOntoBase"/>.
    /// </summary>
    public bool? LastPreFinalPassRebaseWasNoOp { get; private set; }

    /// <summary>
    /// Whether a real (non-no-op) pre-final-pass rebase has landed since the run's own tip was
    /// last gated at full scope (task: a run rebases its branch onto the current base branch,
    /// independent pre-PR review, cycle 1, both lenses) — the signal
    /// <see cref="Hall9k.Daemon.Review.ReviewEngine"/>'s Settling branch uses to force one more
    /// full-scope gate before the run may settle, even on the ordinary "nothing owed" path a clean
    /// Discovery-cycle-1 convergence takes. Deliberately event-sourced rather than a live git HEAD
    /// comparison: a git read fails whenever the run's own worktree cannot be read (adopted onto a
    /// node that never checked it out, or simply gone), and treating that failure as "not yet
    /// gated" would force a redundant full gate on every such settle forever, not just the one
    /// this flag actually exists to catch. Set by a non-no-op <see cref="Events.RunRebasedOntoBase"/>
    /// (whether it applied cleanly on its own or only after a recovery session resolved its
    /// conflict) — or by a no-op one whose <see cref="Events.RunRebasedOntoBase.DecisionsLogRenumbered"/>
    /// is true, since that renumbering commit also moves this branch's tip past whatever was last
    /// gated even though origin's base itself never moved — and cleared by the very next
    /// full-scope <see cref="Events.VerificationPassed"/>, whichever event lands later on the
    /// stream.
    /// </summary>
    public bool PreFinalPassRebaseAwaitingGate { get; private set; }

    /// <summary>
    /// Whether the current <see cref="PreFinalPassRebaseAwaitingGate"/> grant was earned by a
    /// genuine, non-no-op rebase rather than a no-op re-check that only committed a Decisions Log
    /// renumbering (independent pre-PR review, cycle 3, conformance lens): the two share the same
    /// flag above because both leave this branch's tip ungated, but
    /// <see cref="Hall9k.Daemon.Review.ReviewEngine.EligibleForSettlingGateRepair"/> means only the
    /// former by "a pre-final-pass rebase that applies cleanly but breaks the mandatory gate" — a
    /// renumbering-only no-op never moved this branch relative to its base, so a gate failure right
    /// after one is not a rebase-caused break and keeps today's fail-hard contract.
    /// <see cref="LastPreFinalPassRebaseWasNoOp"/> cannot answer this itself: the trailing-no-op
    /// display guard in <see cref="Apply(Events.RunRebasedOntoBase)"/> deliberately leaves it
    /// pointing at an earlier real rebase across a later no-op re-check, which would otherwise make
    /// a renumbering-only no-op that lands after that earlier rebase's own gate already passed
    /// look, wrongly, like the same real rebase is still ungated.
    /// </summary>
    public bool PreFinalPassRebaseAwaitingGateFromRealRebase { get; private set; }

    /// <summary>
    /// Whether a pre-final-pass rebase that needed the recovery session's own judgment has landed
    /// since the last fresh-context review pass read this branch (independent pre-PR review, cycle
    /// 1, conformance lens) — the mirror of <see cref="PreFinalPassRebaseAwaitingGate"/> for the
    /// review guarantee rather than the gate one. A clean git apply never sets this: Brian's
    /// 2026-09-04 ruling is that git applying every commit without a conflict is itself the
    /// evidence that no judgment was exercised, so only a <see cref="Events.RunRebasedOntoBase"/>
    /// whose <see cref="Events.RunRebasedOntoBase.RecoveredByAgentSession"/> is true — an agent
    /// resolved a real conflict by hand — sets it. Without this flag, the Settling branch's
    /// ordinary "nothing owed" settle (a clean Discovery-cycle-1 convergence with no fix dispatched)
    /// let a recovery session's own conflict resolution reach the pull request having never been
    /// read by any fresh-context reviewer, even though <see cref="PreFinalPassRebaseAwaitingGate"/>
    /// forced a build/test gate over it. <see cref="Apply(Events.SettlingGateRepairCompleted)"/> sets
    /// it too (independent pre-PR review, cycle 3, conformance lens): a Settling-gate repair
    /// session's own fix is the identical unreviewed judgment call a recovery session's conflict
    /// resolution is, clean git apply or not, so a repair round earns the same fresh-context review
    /// this flag otherwise reserves for a rebase the recovery session had to resolve by hand. Cleared
    /// the moment any review pass is dispatched
    /// (<see cref="Apply(Events.ReviewDispatched)"/>): for every composition that promises a review
    /// at all, a pre-final-pass rebase can only land while <see cref="ReviewPhase"/> is
    /// <see cref="Domain.Features.Run.ReviewPhase.Settling"/> or a
    /// <see cref="Domain.Features.Run.ReviewPhase.Reverify"/> already bound for
    /// <see cref="ReviewMode.FinalFullPass"/>, so the very next dispatch after it lands is always
    /// the mandatory pass this flag exists to force — never a stale grant left over from an earlier,
    /// unrelated cycle. Composition none never dispatches a review at all, by its own attestation
    /// (<see cref="ReviewStageComposition.None"/>'s own doc), so this flag is simply never consulted
    /// there; it may be set and never cleared for such a run, harmlessly.
    /// </summary>
    public bool PreFinalPassRebaseAwaitingReview { get; private set; }

    /// <summary>Whether the last non-no-op pre-final-pass rebase needed the recovery session rather than applying cleanly on its own.</summary>
    public bool LastPreFinalPassRebaseRecovered { get; private set; }

    /// <summary>The base commit the branch was rebased from, as of the last pre-final-pass rebase attempt.</summary>
    public string? LastPreFinalPassRebaseFromCommit { get; private set; }

    /// <summary>The base commit the branch was rebased onto, as of the last pre-final-pass rebase attempt.</summary>
    public string? LastPreFinalPassRebaseOntoCommit { get; private set; }

    /// <summary>What the last pre-final-pass rebase attempt actually did.</summary>
    public string? LastPreFinalPassRebaseDetail { get; private set; }

    /// <summary>When the last pre-final-pass rebase attempt was made; null until one is.</summary>
    public DateTimeOffset? LastPreFinalPassRebaseAt { get; private set; }

    /// <summary>The in-flight pre-final-pass rebase-recovery session, cleared when its outcome is recorded. Identity for adoption.</summary>
    public Guid? ActiveRebaseRecoverySessionId { get; private set; }
    public int? ActiveRebaseRecoveryProcessId { get; private set; }
    public DateTimeOffset? ActiveRebaseRecoveryProcessStartedAt { get; private set; }
    /// <summary>The model the in-flight rebase-recovery session was spawned on.</summary>
    public AgentModel ActiveRebaseRecoveryModel { get; private set; } = AgentModel.Unknown;
    /// <summary>The base commit the in-flight (or most recently dispatched) recovery session is rebasing from, carried from dispatch to completion.</summary>
    public string? ActiveRebaseRecoveryFromCommit { get; private set; }

    /// <summary>
    /// How many pre-final-pass rebase-recovery sessions this run has dispatched in a row without
    /// ever reaching a clean rebase (task: a run rebases its branch onto the current base branch)
    /// — the independent bound <see cref="Hall9k.Daemon.Review.ReviewEngine"/>'s dispatch checks
    /// before spawning another one, the same shape <see cref="FinalFullPassRounds"/> already gives
    /// the mandatory final pass: a recovery session that ends without actually resolving the
    /// conflict (an undeclared or falsely-optimistic outcome, or a worktree state git itself keeps
    /// refusing) would otherwise send <c>EnsureRebasedBeforeFinalPassAsync</c> straight back to the
    /// identical conflict forever, dispatching a fresh agent session each lap with nothing to stop
    /// it (independent pre-PR review, cycle 1, both lenses). A fresh human grant
    /// (<see cref="Apply(Events.ReviewParkResolved)"/>) resets it, exactly like
    /// <see cref="FinalFullPassRounds"/> already resets there.
    /// </summary>
    public int RebaseRecoveryRounds { get; private set; }

    /// <summary>The in-flight Settling-gate repair session, cleared when its outcome is recorded. Identity for adoption.</summary>
    public Guid? ActiveSettlingGateRepairSessionId { get; private set; }
    public int? ActiveSettlingGateRepairProcessId { get; private set; }
    public DateTimeOffset? ActiveSettlingGateRepairProcessStartedAt { get; private set; }
    /// <summary>The model the in-flight Settling-gate repair session was spawned on.</summary>
    public AgentModel ActiveSettlingGateRepairModel { get; private set; } = AgentModel.Unknown;

    /// <summary>
    /// How many Settling-gate repair sessions this run has dispatched in a row without the
    /// mandatory gate ever passing since (task: a pre-final-pass rebase that applies cleanly but
    /// breaks the mandatory gate gets a repair lap inside the same run instead of failing it) —
    /// the independent bound <see cref="Hall9k.Daemon.Review.ReviewEngine"/>'s dispatch checks
    /// against <see cref="Hall9k.Daemon.DaemonOptions.MaxSettlingGateRepairRounds"/> before
    /// spawning another one, the sibling of <see cref="RebaseRecoveryRounds"/> for this feature's
    /// own cap. A fresh human grant (<see cref="Apply(Events.ReviewParkResolved)"/>'s MergeReady
    /// branch) resets it — but that method's own needs-fixes branch for THIS park deliberately
    /// does not: the task's own criteria say a needs-fixes resolve buys exactly one more round
    /// without resetting the counter, unlike every other capped loop in this file.
    /// </summary>
    public int SettlingGateRepairRounds { get; private set; }

    /// <summary>
    /// The gate output the most recently dispatched (or in-flight) Settling-gate repair session
    /// carried, or was told to carry, OR — once the round cap is spent —
    /// the park's own failure output (<see cref="Apply(Events.SettlingGateRepairCapReached)"/>):
    /// read back by a human-resolve redispatch (<see cref="ReviewPhase.SettlingGateRepairNeeded"/>)
    /// so it can build a fresh session's prompt without paying for another gate run first,
    /// mirroring how a rebase-recovery retry redoes its own cheap git read instead (that
    /// mechanism's check is cheap; this one is a full build/test pass, so re-running it just to
    /// redispatch would be the opposite of narrow). The cap-reached update matters because the
    /// round that actually parked can fail on a DIFFERENT gate than the round before it dispatched
    /// over — without it, the bought round would be handed a stale, already-fixed failure instead
    /// of the one the park (and the human reading it) actually named.
    /// </summary>
    public string? LastSettlingGateRepairOutput { get; private set; }

    /// <summary>
    /// A human's guidance on a spent Settling-gate repair cap (h9k review resolve --needs-fixes),
    /// carried into the one bought repair round <see cref="ReviewPhase.SettlingGateRepairNeeded"/>
    /// dispatches. Mirrors <see cref="PendingRebaseRecoveryGuidance"/>.
    /// </summary>
    public string? PendingSettlingGateRepairGuidance { get; private set; }

    /// <summary>When a human last granted this run's task a fresh closeout budget (h9k pr resolve, Decisions Log #80, backlog 45); null until one lands.</summary>
    public DateTimeOffset? HumanGrantedAt { get; private set; }

    /// <summary>Errored-review re-requests issued for this run; adds to the task's CloseoutAttempts against the shared budget.</summary>
    public int ReviewRerequestCount { get; private set; }

    /// <summary>
    /// Countersign re-requests issued after this run pushed its fixes (Decisions Log #62) — at
    /// most one per run, so this reads as "has this run already asked".
    /// </summary>
    public int ReviewRerequestsAfterFixes { get; private set; }

    /// <summary>
    /// Logins the platform itself has asked to review this run's pull request, either after
    /// an errored review or as a countersign after fixes (Decisions Log #80, backlog 45) — what
    /// CloseoutEngine.HasHumanEngagement excludes from "who newly has a pending review
    /// request" so its own re-request is never read back as a human's.
    /// </summary>
    private readonly List<string> _requestedReviewerLogins = [];
    public IReadOnlyList<string> RequestedReviewerLogins => _requestedReviewerLogins;

    /// <summary>
    /// The pr-review task type's own, deliberately separate track record (PrReviewEngine):
    /// the adversarial lens is this run's ordinary primary session and needs none of these,
    /// so only the conformance lens — dispatched second, once the adversarial findings are on
    /// disk — is tracked here. Never read by ReviewEngine's own cycle/track state machine.
    /// </summary>
    public Guid? PrReviewConformanceSessionId { get; private set; }
    public int? PrReviewConformanceProcessId { get; private set; }
    public DateTimeOffset? PrReviewConformanceProcessStartedAt { get; private set; }
    public bool PrReviewConformanceCompleted { get; private set; }

    /// <summary>
    /// The conformance lens's own resolved model, read back at completion so the session's
    /// TokensRecorded carries the model it actually ran on rather than a re-resolution that could
    /// disagree with it if the node's configuration changed mid-flight (Decisions Log #33).
    /// </summary>
    public AgentModel PrReviewConformanceModel { get; private set; } = AgentModel.Unknown;

    /// <summary>
    /// Set when <see cref="RunBudgetExhausted"/> catches the conformance lens mid-flight
    /// (deliberately not <see cref="ReviewPhase"/>, which pr-review runs never touch — see the
    /// class doc comment). This is what tells <see cref="TokenBudgetRetryEngine"/> to re-enter
    /// the pr-review loop instead of resuming the primary session's process, and what tells
    /// PrReviewEngine's own redispatch check to treat the exhausted session's leftover stream
    /// file as stale rather than a resumable result. Cleared the moment a fresh conformance
    /// session dispatches.
    /// </summary>
    public bool PrReviewConformanceBudgetExhausted { get; private set; }

    /// <summary>
    /// Set when a node-wide launch hold (task: a session that exits at once with no work done
    /// is treated as the node failing to launch sessions) catches the conformance lens mid-flight
    /// — the exact same role <see cref="PrReviewConformanceBudgetExhausted"/> already plays for a
    /// budget park: tells <see cref="RunSupervisor.ResumeLaunchHeldRunAsync"/> to re-enter the
    /// pr-review loop instead of "--resume"-ing the primary session's own (unrelated) process, and
    /// tells <see cref="PrReviewEngine"/>'s own redispatch check to treat the held session's
    /// leftover stream file as stale rather than a resumable result. Cleared the moment a fresh
    /// conformance session dispatches.
    /// </summary>
    public bool PrReviewConformanceLaunchHeld { get; private set; }

    /// <summary>The owner's h9k review resolve --merge-ready verdict on a pr-review park: walk done, close the task.</summary>
    public bool PrReviewDelivered { get; private set; }

    /// <summary>
    /// Every leg/cycle/lens combination that has already spent its one error-result retry
    /// (task: a session that reports an error result is retried once in place) — checked by
    /// <see cref="HasRetriedSessionError"/> before a second consecutive error on the identical
    /// combination is allowed to fail the run rather than retry again. Never cleared: a key is
    /// only ever checked again by the exact same leg/cycle/lens that produced it, and a second
    /// occurrence there is precisely the "second consecutive error" this exists to catch.
    /// </summary>
    private readonly HashSet<string> _errorRetriedLegs = [];

    /// <summary>How many sessions this run has retried once after an error result — a count for the record; <see cref="HasRetriedSessionError"/> is what the engine actually decides on.</summary>
    public int SessionErrorRetryCount { get; private set; }

    /// The pre-PR review loop (log #24): which round of review the run is on, from 1. Every
    /// still-active track shares it — tracks only ever advance together, because the only thing
    /// that advances one is a fix session and gates that every live track then re-reads (log
    /// #63). A track's OWN cycle count is therefore the cycle it last ran at, which is this
    /// number while it is active and frozen at its conclusion once it is not.
    /// </summary>
    public int ReviewCycle { get; private set; }
    /// <summary>
    /// Automatic fix sessions dispatched so far. A count for the record; the loop's bounds are the
    /// per-track cycle caps (log #63). Deliberately never incremented by a human's own
    /// <c>h9k review fixed</c> (task: a human at the wheel takes the fix role herself) — see
    /// <see cref="HumanFixRounds"/>, which counts those separately so neither number ever
    /// misreports who did the work.
    /// </summary>
    public int ReviewFixRuns { get; private set; }
    /// <summary>
    /// Fixes the human applied by hand at interactive mode's review-verdict-to-fix boundary
    /// (<c>h9k review fixed</c>, task: a human at the wheel takes the fix role herself), counted
    /// apart from <see cref="ReviewFixRuns"/>. A count for the record on the same terms that one
    /// is: nothing bounds the loop on it, and a human fix spends no automatic fix budget and no cap
    /// a fix session consumes.
    /// </summary>
    public int HumanFixRounds { get; private set; }
    /// <summary>The current cycle's merged verdict across its lenses (log #59), not any single pass's.</summary>
    public ReviewVerdict LastReviewVerdict { get; private set; } = ReviewVerdict.Unknown;
    public ReviewPhase ReviewPhase { get; private set; } = ReviewPhase.None;

    private readonly List<ReviewTrackOutcome> _concludedReviewTracks = [];
    /// <summary>
    /// The review tracks that have finished, in the order they finished (log #63). Within an
    /// ordinary cycle a concluded track is never dispatched again and is deliberately never
    /// reawakened by the other track's fix sessions — only the mandatory
    /// <see cref="ReviewMode.FinalFullPass"/> cycle immediately before the run may settle
    /// dispatches it anyway (see <see cref="CurrentCycleLenses"/>) and reawakens it via
    /// <see cref="Events.ReviewTrackReactivated"/> when it finds something real (Decisions Log #92).
    /// </summary>
    public IReadOnlyList<ReviewTrackOutcome> ConcludedReviewTracks => _concludedReviewTracks;

    /// <summary>
    /// The tracks a cycle still dispatches: every opening lens that has not concluded (log #63).
    /// Empty means the loop is finished looking. Reads the genuine, current state — a track this
    /// property has ever reported concluded stays reported that way, including a track a
    /// <see cref="ReviewMode.FinalFullPass"/> cycle just reconfirmed clean or reawakened, which is
    /// why <c>ReviewEngine.SettleAsync</c> and the fix-cap check read through THIS property
    /// rather than <see cref="CurrentCycleLenses"/>: by the time either runs, this cycle's own
    /// conclusions (or reactivation) have already landed, and re-deriving "both, unconditionally"
    /// would re-conclude a track this same cycle already gave a real answer.
    /// </summary>
    public IReadOnlyList<ReviewLens> ActiveReviewLenses =>
        [.. ReviewStageComposition.OpeningLenses().Where(lens => !_concludedReviewTracks.Any(track => track.Lens.Covers(lens)))];

    /// <summary>
    /// The lenses the CURRENT cycle's own dispatch, top-up, and conclusion bookkeeping must
    /// account for (task: review cycles after the first) — <see cref="ActiveReviewLenses"/> for
    /// every mode except <see cref="ReviewMode.FinalFullPass"/>, where it is every real lens
    /// regardless of conclusion: that cycle's whole job is to read every lens fresh, including a
    /// dormant one, so the crash-recovery top-up (<c>ReviewEngine.DispatchMissingPassesAsync</c>),
    /// the cycle-conclusion check (<see cref="DeriveReviewPhase"/>,
    /// <c>ReviewEngine.RecordReviewPassAsync</c>'s own <c>cycleConcluded</c>), and the per-track
    /// planning (<c>ReviewEngine.PlanCycleAsync</c>) all expect a pass — or a plan — for both.
    /// Deliberately NOT what <c>SettleAsync</c> or the fix-cap check read: once this cycle's own
    /// conclusions land, <see cref="ActiveReviewLenses"/> is what genuinely answers "who is still
    /// owed a look," and this property would wrongly keep answering "both" for the rest of the
    /// cycle's lifetime, since <see cref="CurrentCycleMode"/> itself does not change until the
    /// next cycle's own dispatch.
    /// </summary>
    public IReadOnlyList<ReviewLens> CurrentCycleLenses =>
        CurrentCycleMode == ReviewMode.FinalFullPass ? ReviewStageComposition.OpeningLenses() : ActiveReviewLenses;

    /// <summary>
    /// The shape the most recently dispatched review cycle took (task: review cycles after the
    /// first) — <see cref="ReviewMode.Discovery"/> by default, including for every stream written
    /// before this field existed. Set once per cycle, at its first <see cref="ReviewDispatched"/>,
    /// and unchanged by anything else that cycle records.
    /// </summary>
    public ReviewMode CurrentCycleMode { get; private set; } = ReviewMode.Discovery;

    /// <summary>
    /// <see cref="CurrentCycleMode"/> as it stood immediately before the current cycle started
    /// (task: review cycles after the first, cycle-4 conformance finding) — what a
    /// <see cref="ReviewMode.Verify"/> pass's prompt uses to say honestly whether the prior cycle it
    /// is quoting findings from read the branch in full (<see cref="ReviewMode.Discovery"/> or
    /// <see cref="ReviewMode.FinalFullPass"/>) or was itself a delta-scoped <see cref="ReviewMode.Verify"/>
    /// pass, mirroring <see cref="PriorCycleHeadSha"/>'s own capture-once-per-cycle bookkeeping so a
    /// same-cycle top-up dispatch still reads the cycle before the one it is topping up.
    /// </summary>
    public ReviewMode PriorCycleMode { get; private set; } = ReviewMode.Discovery;

    /// <summary>
    /// The most recently dispatched review cycle's own <see cref="Events.ReviewDispatched.SinceSha"/>
    /// (independent pre-PR review, cycle 1 adversarial finding) — null unless
    /// <see cref="CurrentCycleMode"/> is <see cref="ReviewMode.Verify"/> (the prior cycle's own tip,
    /// the boundary its delta read is scoped since), is <see cref="ReviewMode.FinalFullPass"/> and
    /// that cycle was itself scoped to the commits since an earlier full-scope read, or is
    /// <see cref="ReviewMode.Discovery"/> and is a ReviewFeedback or FailingChecks follow-up's own
    /// opening cycle scoped to the seeded pull request head (task: a lap reviews only what it
    /// changed). Mirrors <see cref="CycleHeadSha"/>'s own capture-once-per-cycle bookkeeping,
    /// including for a crash-recovery top-up into the same cycle.
    /// </summary>
    public string? CycleSinceSha { get; private set; }

    /// <summary>
    /// <see cref="CycleSinceSha"/> as it stood immediately before the current cycle started —
    /// paired with <see cref="PriorCycleMode"/> so a <see cref="ReviewMode.Verify"/> pass's prompt
    /// (<c>AgentPromptBuilder.BuildReviewVerify</c>) can tell a genuinely full FinalFullPass read
    /// from a scoped one instead of assuming every non-Verify prior cycle read the branch in full.
    /// Captured once, the same capture-once-per-cycle bookkeeping <see cref="PriorCycleHeadSha"/>
    /// already uses.
    /// </summary>
    public string? PriorCycleSinceSha { get; private set; }

    /// <summary>
    /// The worktree's `git rev-parse HEAD` as of the most recently dispatched review cycle's own
    /// dispatch (task: review cycles after the first). Recorded so the NEXT cycle, if it turns out
    /// to be a <see cref="ReviewMode.Verify"/> cycle, has this cycle's tip available as
    /// <see cref="PriorCycleHeadSha"/> once it starts. Null when it was never recorded (a stream
    /// written before this field existed) or could not be read at dispatch time.
    /// </summary>
    public string? CycleHeadSha { get; private set; }

    /// <summary>
    /// <see cref="CycleHeadSha"/> as it stood immediately before the current cycle started (task:
    /// review cycles after the first) — what a <see cref="ReviewMode.Verify"/> cycle's prompt
    /// points its "commits since the prior cycle" instruction at. Captured once, when the current
    /// cycle's first <see cref="ReviewDispatched"/> lands, and held constant for the rest of that
    /// cycle's lifetime — including a crash-recovery top-up dispatch into the same cycle
    /// (<c>ReviewEngine.DispatchMissingPassesAsync</c>), which must still point at the cycle
    /// <i>before</i> the one it is topping up rather than at <see cref="CycleHeadSha"/>, which by
    /// then already holds this cycle's own tip. Null when the prior cycle's head was never recorded
    /// or could not be read; the engine falls back to a full-range diff instruction rather than
    /// guessing at a boundary.
    /// </summary>
    public string? PriorCycleHeadSha { get; private set; }

    /// <summary>
    /// The worktree HEAD of the most recent cycle that actually DELIVERED a readable full-scope
    /// verdict from every lens it dispatched — a genuinely full-scope <see cref="ReviewMode.Discovery"/>
    /// or <see cref="ReviewMode.FinalFullPass"/> cycle, never a <see cref="ReviewMode.Verify"/> one,
    /// which only ever reads a delta (task: the mandatory FinalFullPass reads only the commits no
    /// full-scope pass has already read). A ReviewFeedback or FailingChecks follow-up's own opening
    /// Discovery cycle does not count when it was itself scoped to the lap's own change (task: a lap
    /// reviews only what it changed) — see <see cref="SetReviewPhaseAndLatchFullScopeBoundary"/>'s
    /// own doc. This latches when <see cref="DeriveReviewPhase"/> first
    /// reports every current-cycle lens answered (<see cref="ReviewPhase.FixNeeded"/> or
    /// <see cref="ReviewPhase.Settling"/>), never at the cycle's own dispatch (independent pre-PR
    /// review, cycle 1 adversarial finding): a cycle dispatched at head H1 whose verdict never
    /// resolves — parked on <see cref="ReviewPhase.VerdictMissing"/> past its one re-prompt, or a
    /// lens never even topped up before the run parked — must not narrow a later full-scope read
    /// past commits no reviewer was ever observed to have read; only an actually concluded
    /// full-scope cycle earns that trust. Held constant across every <see cref="ReviewMode.Verify"/>
    /// cycle in between — unlike <see cref="CycleHeadSha"/>, which moves on every cycle regardless
    /// of mode — so a <see cref="ReviewMode.FinalFullPass"/> dispatched after several
    /// fix-and-verify rounds still finds the boundary of the last pass that actually delivered a
    /// full-scope verdict, not just the immediately preceding cycle. "Full scope" is the chain
    /// rather than one pass's own range: a scoped <see cref="ReviewMode.FinalFullPass"/> reads from
    /// the previous full-scope pass's own head, so successive full-scope reads tile the branch with
    /// no gap. Null until the first full-scope cycle delivers a verdict, or when the concluding
    /// cycle's own HeadSha could not be read at dispatch time — the engine falls back to a
    /// full-range diff instruction rather than guessing at a boundary (the same
    /// degrade-rather-than-guess rule <c>TestScopeResolver</c> already follows).
    /// </summary>
    public string? LastFullScopeReviewHeadSha { get; private set; }

    private readonly List<ReviewResidual> _reviewResiduals = [];
    /// <summary>Every finding the tracks ended on without a reviewer confirming it resolved (log #63).</summary>
    public IReadOnlyList<ReviewResidual> ReviewResiduals => _reviewResiduals;

    /// <summary>
    /// How the review ended, once it has (log #63). Unknown while the loop is still running, and
    /// unknown forever for a run whose review was already in flight before tracks existed —
    /// that stream never recorded the distinction and this does not invent it.
    /// </summary>
    public ReviewSettlement ReviewSettlement { get; private set; } = ReviewSettlement.Unknown;

    /// <summary>
    /// The cycle the current automatic budget counts from. Zero until a human resolves a park
    /// with needs-fixes, which — like a manual pr resolve — is a fresh grant (log #22): the
    /// per-track cycle caps are measured from there, so the run does not re-park on the very
    /// next cycle for a budget the human just renewed.
    /// </summary>
    public int ReviewBudgetBaseCycle { get; private set; }

    private readonly Dictionary<ReviewLens, int> _trackReactivatedAtCycle = [];

    /// <summary>
    /// The cycle a given track's own cap counts from (task: review cycles after the first) —
    /// ordinarily <see cref="ReviewBudgetBaseCycle"/>, but bumped to whichever cycle a
    /// <see cref="Events.ReviewTrackReactivated"/> most recently reawakened this lens: the
    /// mandatory <see cref="ReviewMode.FinalFullPass"/> can revive a track that had already gone
    /// dormant cycles ago, and measuring its cap from the run's absolute cycle count would count
    /// every cycle the OTHER track spent alone against a lens that was not even being asked
    /// anything for most of them — capping it before its own reawakened work ever gets a fix
    /// session dispatched. <c>Math.Max</c> is what lets a human's later fresh grant
    /// (<see cref="ReviewBudgetBaseCycle"/> moving forward on a needs-fixes park resolution)
    /// still win over an earlier reactivation without a separate reset.
    /// </summary>
    public int TrackBudgetBaseCycle(ReviewLens lens) =>
        Math.Max(ReviewBudgetBaseCycle, _trackReactivatedAtCycle.GetValueOrDefault(lens));

    /// <summary>
    /// How many cycles this run has dispatched as the mandatory <see cref="ReviewMode.FinalFullPass"/>
    /// (task: review cycles after the first, cycle-3 finding) — an independent bound alongside the
    /// per-track cycle caps, because <see cref="Events.ReviewTrackReactivated"/> deliberately resets
    /// <see cref="TrackBudgetBaseCycle"/> (that field's own doc says why), which means a track the
    /// final pass keeps reawakening never trips its own cap on its own. Counted per cycle, not per
    /// lens: a FinalFullPass cycle dispatches two passes but is one round of the mandatory read.
    /// </summary>
    public int FinalFullPassRounds { get; private set; }

    /// <summary>
    /// How many times <see cref="Events.ReviewTrackReactivated"/> has actually landed on this run's
    /// stream. Never reset — the park text that reads this (<c>ReviewEngine.FinalFullPassCapParkReason</c>)
    /// needs to know whether a track was ever genuinely reawakened, not just whether the mandatory
    /// pass ran more than once: those are different claims, and the park text must never assert the
    /// former on evidence of only the latter (cycle-3 cap-park finding — never guess at unobserved
    /// facts).
    /// </summary>
    public int ReviewTrackReactivations { get; private set; }

    /// <summary>This cycle's findings that are still owed a fix session — the loop's "is there anything to fix" (log #63).</summary>
    public int PendingFixFindings =>
        _completedReviewPasses.Sum(pass =>
            pass.Findings.Count(finding => finding.Disposition == ReviewFindingDisposition.Fix));

    /// <summary>Whether this cycle recorded per-pass milestones. False for a pre-lens stream, whose one ReviewCompleted IS the cycle.</summary>
    private bool _cycleHasPassMilestones;

    /// <summary>Whether a human's merge-ready park resolution is what ended the loop, rather than a clean reviewer.</summary>
    private bool _humanEndedTheLoop;

    /// <summary>
    /// Whether a human's own merge-ready park resolution is what is ending the loop (task: review
    /// cycles after the first) — the mandatory <see cref="ReviewMode.FinalFullPass"/> before the run
    /// may settle does not apply here: a human overruling the automatic loop already looked, or
    /// deliberately chose not to, and dispatching another agent pass over their explicit verdict
    /// would be presumptuous rather than thorough.
    /// </summary>
    public bool HumanEndedTheLoop => _humanEndedTheLoop;

    private readonly List<ReviewPassSession> _inFlightReviewPasses = [];
    /// <summary>
    /// This cycle's review passes still awaiting a result, in dispatch order (log #59) —
    /// the identities the daemon adopts after a restart. Empty between cycles and while a
    /// fix session holds the loop.
    /// </summary>
    public IReadOnlyList<ReviewPassSession> InFlightReviewPasses => _inFlightReviewPasses;

    private readonly List<ReviewPassResult> _completedReviewPasses = [];
    /// <summary>
    /// This cycle's review passes whose verdict is recorded, in dispatch order. A re-prompted
    /// pass replaces its own earlier result in place rather than appearing twice: the cycle
    /// has one answer per lens.
    /// </summary>
    public IReadOnlyList<ReviewPassResult> CompletedReviewPasses => _completedReviewPasses;

    /// <summary>The in-flight fix session, cleared when its outcome is recorded. Identity for adoption.</summary>
    public Guid? ActiveFixSessionId { get; private set; }
    public int? ActiveFixProcessId { get; private set; }
    public DateTimeOffset? ActiveFixProcessStartedAt { get; private set; }
    /// <summary>The model the in-flight fix session was spawned on.</summary>
    public AgentModel ActiveFixSessionModel { get; private set; } = AgentModel.Unknown;

    /// <summary>
    /// The cycle of the most recently dispatched fix round (task: a second fix round over the
    /// same findings), null before this run's first fix session ever dispatches. Paired with
    /// <see cref="LastFixRoundHumanFindings"/> to tell a genuinely new round from a mechanical
    /// redispatch of the very same one (a budget-exhaustion retry re-enters FixNeeded with the
    /// cycle and <see cref="PendingHumanFindings"/> both unchanged) — the escalation trigger is
    /// evaluated fresh only for the former.
    /// </summary>
    public int? LastFixRoundCycle { get; private set; }

    private bool _fixDispatchedThisCycle;

    /// <summary>
    /// Whether a fix session has dispatched since the CURRENT tracked cycle started (task: review
    /// cycles after the first) — reset by <see cref="StartCycleIfNew"/> the moment a fresh
    /// <see cref="Events.ReviewDispatched"/> starts a new cycle, unlike <see cref="LastFixRoundCycle"/>,
    /// which is a plain cycle number and can echo a fix from a logically earlier cycle that happens
    /// to share the same number: a run adopted mid-review with no <see cref="Events.ReviewDispatched"/>
    /// ever recorded keeps <see cref="ReviewCycle"/> at 0 through a fix that itself labels its own
    /// events "cycle 1", so the very next fresh Discovery dispatch reuses that same label — comparing
    /// <see cref="LastFixRoundCycle"/> to <see cref="ReviewCycle"/> directly would then read as "a fix
    /// already ran on this cycle" for a fix that in fact predates it. This field cannot make that
    /// mistake: it answers only for fixes dispatched after the currently-tracked cycle's own start.
    /// </summary>
    public bool FixDispatchedThisCycle => _fixDispatchedThisCycle;

    /// <summary>
    /// Whether the most recently recorded <see cref="Events.VerificationPassed"/> ran every
    /// `dotnet test`-shaped gate at full scope (task: a fix cycle's verification gate) — set from
    /// the event itself, never re-derived, since only <c>VerificationRunner.VerifyAsync</c>'s own
    /// caller ever knows whether a nominally scoped gate actually fell back to full (an unmapped touched
    /// file, or a scoped filter that intersected to nothing). Paired with
    /// <see cref="LastGateHeadSha"/>: together they are what lets <c>ReviewEngine</c>'s Settling
    /// step recognize "this exact tip already had a full run" and skip a second one, rather than
    /// re-deriving it from <see cref="CurrentCycleMode"/> alone, which cannot tell a scoped
    /// Verify cycle that never fell back from one that did.
    /// </summary>
    public bool LastGateRanFullScope { get; private set; }

    /// <summary>The worktree HEAD <see cref="LastGateRanFullScope"/>'s gate ran against; null when it could not be read. Never trusted alone — a full scope over a HEAD that has since moved is not a full scope over the tip about to settle.</summary>
    public string? LastGateHeadSha { get; private set; }

    /// <summary>
    /// The project's <see cref="Hall9k.Domain.Features.Project.VerifyCommand.Fingerprint"/> at the
    /// time <see cref="LastGateRanFullScope"/>'s gate ran (Copilot review, PR #62). A HEAD match
    /// alone cannot tell "the same gates ran" from "a human changed the project's verify commands
    /// mid-run while the tip stayed put" — this is the other half of that check, compared against
    /// a fresh read of the project's current gates at settling time. Null on any stream written
    /// before this field existed — read by <c>ReviewEngine.VerifyCommandsFingerprintMatchesAsync</c>
    /// as a match rather than a mismatch, since an unrecorded fingerprint is an unobserved fact, not
    /// an observed change (independent pre-PR review, cycle 3, adversarial lens).
    /// </summary>
    public string? LastGateVerifyCommandsFingerprint { get; private set; }

    /// <summary>The <see cref="PendingHumanFindings"/> value in force when <see cref="LastFixRoundCycle"/>'s round dispatched — see that field's own doc for why this pairing matters.</summary>
    public string? LastFixRoundHumanFindings { get; private set; }

    private readonly List<string> _lastFixRoundFindingLocations = [];
    /// <summary>
    /// The finding locations the most recent AUTOMATED-findings fix round was dispatched over —
    /// what the NEXT fix round is compared against to detect a repeat (task: a second fix round
    /// over the same findings). NOT necessarily <see cref="LastFixRoundCycle"/>'s own round: a
    /// human-findings round (<see cref="PendingHumanFindings"/> non-blank when it dispatches)
    /// advances <see cref="LastFixRoundCycle"/> without replacing this list, because a human's
    /// own reason is never the CURRENT side of that round's own comparison either — so while a
    /// human round is what most recently dispatched, this can still be naming an earlier,
    /// automated round's locations rather than that round's own. Carries forward only that one
    /// automated round, not the whole history, which is what makes de-escalation automatic: a
    /// round whose findings clear this list simply stops matching the next time an automated
    /// comparison is made, with no separate reset.
    /// </summary>
    public IReadOnlyList<string> LastFixRoundFindingLocations => _lastFixRoundFindingLocations;

    /// <summary>Whether the most recently dispatched fix session ran on the review role's model instead of the fix role's, and why (task: a second fix round over the same findings).</summary>
    public bool LastFixSessionEscalated { get; private set; }

    /// <summary>Non-null exactly when <see cref="LastFixSessionEscalated"/> is true.</summary>
    public string? LastFixSessionEscalationReason { get; private set; }

    /// <summary>
    /// This cycle's findings dispositioned <see cref="ReviewFindingDisposition.Fix"/>, by
    /// location, with an unplaced finding (blank location — an unstructured needs-fixes verdict,
    /// or the placeholder <see cref="ReviewFindingRecord"/> a fix session's prompt still carries
    /// even then) excluded: it names nowhere and cannot be shown to repeat, or fail to repeat,
    /// anything (the same reading <see cref="ReviewFindingLocations.SamePlace"/> already applies
    /// everywhere else in the loop). What a fix session dispatched over this cycle's own
    /// automated findings is actually being asked to fix, and so the basis for detecting the NEXT
    /// round repeating it.
    /// </summary>
    public IReadOnlyList<string> CurrentCycleFixFindingLocations =>
        [.. _completedReviewPasses
            .SelectMany(pass => pass.Findings)
            .Where(finding => finding.Disposition == ReviewFindingDisposition.Fix && finding.Location.IsNotBlank())
            .Select(finding => finding.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    /// <summary>The highest cycle whose verdict re-prompt was already spent (0 = never). One re-prompt per CYCLE, then park.</summary>
    public int VerdictRepromptedCycle { get; private set; }
    /// <summary>
    /// Which lens actually received <see cref="VerdictRepromptedCycle"/>'s one re-prompt
    /// (<see cref="ReviewLens.Unknown"/> if none yet). A cycle can end its budget with more than
    /// one verdict-less pass, and only this one of them was ever resumed — the rest reach the
    /// park having never themselves been re-prompted, which the park reason needs to say
    /// accurately rather than crediting every verdict-less lens with a re-prompt only one of
    /// them got.
    /// </summary>
    public ReviewLens VerdictRepromptedLens { get; private set; } = ReviewLens.Unknown;
    /// <summary>Human findings from a needs-fixes park resolution, consumed by the next fix dispatch.</summary>
    public string? PendingHumanFindings { get; private set; }

    /// <summary>
    /// Where the pipeline stood when a park interrupted it, read off the stream rather than
    /// carried on the event (Unknown until a park happens). The two parks reach the same
    /// state from opposite places: the review loop's own parks land from UnderReview, after
    /// reviewers actually read the diff, while a thread-dispute park (Decisions Log #62)
    /// lands from Verifying, before the gates ever ran. A merge-ready resolution needs that
    /// difference — it may overrule findings that exist, but it can never stand in for gates
    /// and a review that never happened.
    /// </summary>
    public RunState ParkedFromState { get; private set; } = RunState.Unknown;

    /// <summary>
    /// The <see cref="ReviewPhase"/> this run was about to act on when the park interrupted it,
    /// captured the same way <see cref="ParkedFromState"/> already is, from
    /// <see cref="ReviewPhase"/> immediately before <see cref="Apply(Events.ReviewParked)"/>
    /// overwrites it. Two consumers read it for two different reasons: for an interactive gate
    /// (<see cref="ParkedIsInteractiveGate"/>), a bare <c>h9k review proceed</c> carries no
    /// verdict of its own, so <see cref="Apply(Events.ReviewBoundaryApproved)"/> resumes the loop
    /// by restoring exactly this phase rather than deriving a new one the way
    /// <see cref="Apply(Events.ReviewParkResolved)"/>'s verdict-bearing branches do; and
    /// <see cref="ParkedFromState"/> alone cannot tell a disputed pre-final-pass rebase park
    /// (task: a run rebases its branch onto the current base branch) apart from every other park
    /// that also lands from <see cref="RunState.UnderReview"/>, so <c>Apply(ReviewParkResolved)</c>
    /// also uses this as the narrower discriminator that routes a rebase-recovery dispute's
    /// resolution to its own dedicated path instead of the ordinary FixNeeded one.
    /// </summary>
    public ReviewPhase ParkedFromReviewPhase { get; private set; } = ReviewPhase.None;

    /// <summary>
    /// Whether the park just recorded is interactive mode's own routine boundary gate rather than
    /// a disputed finding or a cap/budget park (task: interactive mode becomes a recorded property
    /// of the task) — mirrors <see cref="Events.ReviewParked.IsInteractiveGate"/>. What
    /// <c>ReviewProceedCommand</c> checks before accepting a bare <c>h9k review proceed</c>:
    /// <c>h9k review resolve</c> keeps working unchanged on every park regardless of this flag.
    /// </summary>
    public bool ParkedIsInteractiveGate { get; private set; }

    /// <summary>
    /// Whether the park that produced the CURRENT <see cref="PendingHumanFindings"/> was
    /// interactive mode's own routine boundary gate rather than a disputed finding or a cap/budget
    /// park — captured once, at the exact moment <see cref="Apply(Events.ReviewParkResolved)"/>
    /// sets <see cref="PendingHumanFindings"/> from <see cref="ParkedIsInteractiveGate"/>, and
    /// carried alongside it through the identical lifecycle: cleared together at
    /// <see cref="Apply(Events.ReviewFixCompleted)"/>, left untouched together across
    /// <see cref="Apply(Events.RunBudgetExhausted)"/>'s redispatch-at-FixNeeded, and never
    /// overwritten by an unrelated park in between (independent pre-PR review, cycle 3, adversarial
    /// lens). An earlier version of this field instead mirrored <c>ReviewParked.IsInteractiveGate</c>
    /// directly and was overwritten by ANY later <see cref="Events.ReviewParked"/> — including one
    /// belonging to an entirely different park, reached only because
    /// <see cref="Apply(Events.RunBudgetExhausted)"/> leaves <see cref="PendingHumanFindings"/>
    /// intact while cycling the run back through FixNeeded — which let a genuine rebase-dispute
    /// resolution's own findings get misrouted to the ordinary review-fix prompt once an
    /// intervening routine boundary park re-set the shared flag. Pairing this flag to the findings
    /// it actually describes, rather than reading it fresh off whatever <see cref="Events.ReviewParked"/>
    /// happened most recently, is what <see cref="Hall9k.Daemon.Review.ReviewEngine.DispatchFixSessionAsync"/>
    /// needs to still tell a genuine rebase-dispute resolution (cycle 0, <c>FollowUpKind.Rebase</c>,
    /// human findings present) apart from a human's <c>--needs-fixes</c> redirect of the interactive
    /// gate's own "build done to review" boundary, which carries the identical cycle and follow-up
    /// kind but nothing disputed.
    /// </summary>
    public bool PendingHumanFindingsFromInteractiveGate { get; private set; }

    /// <summary>
    /// True the moment a fresh <c>h9k review proceed</c> (or, on the FixNeeded boundary, an
    /// <c>h9k review resolve</c>) has cleared THIS run's own interactive-mode gate for whichever
    /// boundary is next in line, false again the instant the corresponding dispatch actually
    /// consumes it (<see cref="Apply(Events.ReviewDispatched)"/>,
    /// <see cref="Apply(Events.ReviewFixDispatched)"/>) — one shot per boundary, so a task with
    /// <c>InteractiveModeEnabled</c> is asked again at the very next one rather than sailing
    /// through the rest of the run on a single approval (task: interactive mode becomes a
    /// recorded property of the task). A human's own fix
    /// (<see cref="Apply(Events.ReviewHumanFixApplied)"/>, task: a human at the wheel takes the fix
    /// role herself) consumes it on the same terms a dispatched fix session does: that fix IS the
    /// answer at the review-verdict-to-fix boundary, so the fix-to-re-review boundary that follows
    /// asks its own question rather than inheriting that approval.
    /// </summary>
    public bool InteractiveGateCleared { get; private set; }

    /// <summary>
    /// Whether granting <c>--needs-fixes</c> against the park just recorded cannot clear it (a
    /// per-track cap-0 takeover park, a final-full-pass cap-0 park, or the lifetime-budget park)
    /// — mirrors <see cref="Events.ReviewParked.NeedsFixesOffersNoProgress"/> and
    /// <see cref="Projections.RunDetails.ParkedNeedsFixesOffersNoProgress"/> so
    /// <c>ReviewResolveCommand</c>'s own outcome message agrees with what
    /// <c>h9k status</c> already says, instead of unconditionally promising progress a cap or
    /// budget still blocks.
    /// </summary>
    public bool ParkedNeedsFixesOffersNoProgress { get; private set; }

    /// <summary>
    /// The disagreements a changes-requested fix lap parked rather than answering itself (task: a
    /// changes-requested pull-request review from a human becomes a fix lap) — each with the
    /// reviewer's point, the session's reasoning, and the reply it drafted for the implementer to
    /// send, edit, or drop. Empty on every other park, and emptied again the moment the park is
    /// resolved: a drafted reply describes the park it was drafted for and nothing after it.
    /// </summary>
    public IReadOnlyList<ReviewDisagreement> ParkedDisagreements { get; private set; } = [];

    /// <summary>
    /// Whether the park just recorded is a changes-requested lap's disagreement park, which is
    /// what makes <c>h9k review resolve</c> require one of its three reply choices before it will
    /// let the run continue. Kept as its own flag rather than inferred from
    /// <see cref="ParkedDisagreements"/> being non-empty: a session that emitted the disagreement
    /// marker without a single parseable block still parked for exactly this reason, and the
    /// implementer still has to say what the reviewer hears — reading an unparseable park as an
    /// ordinary one would silently skip that question.
    /// </summary>
    public bool ParkedOnReviewDisagreement { get; private set; }

    /// <summary>Human guidance from a resolved pre-final-pass rebase-recovery dispute (h9k review resolve --needs-fixes), consumed by the next recovery-session dispatch.</summary>
    public string? PendingRebaseRecoveryGuidance { get; private set; }

    /// <summary>Whether this run handed anything down at true closeout, and when not, why (log #36).</summary>
    public HandoffOutcome HandoffOutcome { get; private set; } = HandoffOutcome.Unknown;

    /// <summary>The bounded handoff text; null whenever the outcome records an absence.</summary>
    public string? HandoffSummary { get; private set; }

    /// <summary>Synthesis sessions dispatched for this run's own starting context (log #36).</summary>
    public int ContextSynthesisSessions { get; private set; }

    /// <summary>Whether the last synthesis pass produced a usable document; false also means "fell back to raw".</summary>
    public bool ContextSynthesized { get; private set; }

    private readonly List<string> _failedGates = [];
    public IReadOnlyList<string> FailedGates => _failedGates;

    /// <summary>Infrastructure-classified gate retries this run has spent (backlog 53) — a count for the record.</summary>
    public int GateRetries { get; private set; }

    private readonly List<string> _failingChecks = [];
    public IReadOnlyList<string> FailingChecks => _failingChecks;

    /// <summary>The operator's Claude Code session id, from the most recent <see cref="InteractiveSessionStarted"/>.</summary>
    public Guid? InteractiveClaudeSessionId { get; private set; }

    /// <summary>How many interactive attach/detach cycles this run has recorded (h9k task work, held and re-entered).</summary>
    public int InteractiveSessionCount { get; private set; }

    public void Apply(RunDispatched @event)
    {
        Id = @event.Id;
        TaskId = @event.TaskId;
        NodeId = @event.NodeId;
        OwnerId = @event.OwnerId;
        LeaseGeneration = @event.LeaseGeneration;
        SessionId = @event.SessionId;
        WorktreePath = @event.WorktreePath;
        Branch = @event.Branch;
        RunDirectory = @event.RunDirectory.IsNotBlank() ? @event.RunDirectory : RunPaths.GlobalDirectory(@event.Id);
        PrReviewBaseRefName = @event.PrReviewBaseRefName;
        BaseBranch = @event.BaseBranch;
        BaseCommit = @event.BaseCommit;
        ExecutorMode = @event.ExecutorMode;
        Model = @event.Model ?? AgentModel.Unknown;
        ReviewStageComposition = @event.ReviewStageComposition ?? ReviewStageComposition.FullPipeline;
        DispatchedAt = @event.DispatchedAt;
        IsFollowUp = @event.IsFollowUp;
        OpeningReviewSinceSha = @event.OpeningReviewSinceSha;
        State = RunState.Dispatched;
    }

    /// <summary>See the event's own doc: a reconstructed stream for a run that never actually dispatched.</summary>
    public void Apply(RunRecordReconstructed @event)
    {
        Id = @event.Id;
        TaskId = @event.TaskId;
        NodeId = @event.NodeId;
        OwnerId = @event.OwnerId;
        PullRequestUrl = @event.PullRequestUrl;
        PullRequestNumber = @event.PullRequestNumber;
        DispatchedAt = @event.ReconstructedAt;
        State = RunState.Dispatched;
    }

    public void Apply(RunProcessStarted @event)
    {
        ProcessId = @event.ProcessId;
        ProcessStartedAt = @event.ProcessStartedAt;
        State = RunState.Running;
    }

    public void Apply(RunResumed @event)
    {
        ProcessId = @event.ProcessId;
        ProcessStartedAt = @event.ProcessStartedAt;
        State = RunState.Running;
    }

    public void Apply(AgentSessionCompleted @event)
    {
        State = RunState.Verifying;
        if (@event.DeliveredByNodeId is { } deliveredByNodeId)
        {
            NodeId = deliveredByNodeId;
        }
    }

    public void Apply(TokensRecorded @event)
    {
        InputTokens += @event.InputTokens;
        CacheReadInputTokens += @event.CacheReadInputTokens;
        CacheCreationInputTokens += @event.CacheCreationInputTokens;
        OutputTokens += @event.OutputTokens;
        if (@event.CostUsd is not null)
        {
            CostUsd = (CostUsd ?? 0m) + @event.CostUsd.Value;
        }
    }

    public void Apply(VerificationFailed @event)
    {
        _failedGates.Clear();
        _failedGates.AddRange(@event.FailedGates);
    }

    public void Apply(VerificationPassed @event)
    {
        _failedGates.Clear();
        LastGateRanFullScope = @event.RanFullScope;
        LastGateHeadSha = @event.HeadSha;
        LastGateVerifyCommandsFingerprint = @event.VerifyCommandsFingerprint;
        // A full-scope gate, wherever it landed on the stream relative to the last real rebase,
        // is a gate that read whatever the worktree held at that moment — see
        // PreFinalPassRebaseAwaitingGate's own doc for why this is event order, not a live git
        // comparison.
        if (@event.RanFullScope)
        {
            PreFinalPassRebaseAwaitingGate = false;
            PreFinalPassRebaseAwaitingGateFromRealRebase = false;
            // The Settling-gate repair cap's own confirmed-fixed signal (task: a pre-final-pass
            // rebase that applies cleanly but breaks the mandatory gate gets a repair lap inside
            // the same run instead of failing it): a full-scope pass only ever lands here when the
            // mandatory gate itself passed (a failure appends VerificationFailed, never this
            // event), so this is the identical "confirmed gone, not just claimed" reset
            // RebaseRecoveryRounds already gets from a confirmed no-op — without it, a round spent
            // fixing an earlier rebase's gate failure would still count against a later, unrelated
            // one later in this same run's lifecycle (another base move, another break) instead of
            // that failure earning its own fair cap.
            SettlingGateRepairRounds = 0;
        }
    }

    public void Apply(GateRetried @event) => GateRetries++;

    public void Apply(ReviewDispatched @event)
    {
        StartCycleIfNew(@event.Cycle, @event.Mode ?? ReviewMode.Discovery, @event.HeadSha, @event.SinceSha);
        AddInFlightPass(
            @event.Lens ?? ReviewLens.Unknown, @event.SessionId, @event.SessionId,
            @event.ProcessId, @event.ProcessStartedAt, @event.Model ?? AgentModel.Unknown, CurrentCycleMode);
        ReviewPhase = ReviewPhase.AwaitingVerdict;
        State = RunState.UnderReview;
        // The review-dispatch/re-review boundary's own approval is spent the moment the dispatch
        // it bought actually lands — the very next boundary (review verdict to fix) asks fresh
        // (task: interactive mode becomes a recorded property of the task).
        InteractiveGateCleared = false;
        // A pre-final-pass rebase can only land while ReviewPhase is Settling or a Reverify already
        // bound for FinalFullPass (see PreFinalPassRebaseAwaitingReview's own doc), so any review
        // dispatch reaching here is always the fresh-context read that flag exists to force —
        // discharged the moment it is actually dispatched, not only once it concludes, the same as
        // every other cap and gate in this file resets on the grant rather than the outcome.
        PreFinalPassRebaseAwaitingReview = false;
    }

    public void Apply(ReviewPassCompleted @event)
    {
        ReviewLens lens = @event.Lens ?? ReviewLens.Unknown;
        ReviewPassSession? pass = _inFlightReviewPasses.FirstOrDefault(inFlight => inFlight.Lens == lens);
        // The transcript session, not this leg's artifact identity: a re-prompted pass is
        // resumed under a new artifact id, and the resume target stays the original session.
        RecordPassResult(
            lens, pass?.TranscriptSessionId, pass?.Model ?? AgentModel.Unknown, @event.Verdict,
            FindingsOf(@event.Findings, @event.Verdict), @event.Mode ?? ReviewMode.Discovery);
        _inFlightReviewPasses.RemoveAll(inFlight => inFlight.Lens == lens);
        _cycleHasPassMilestones = true;
        SetReviewPhaseAndLatchFullScopeBoundary(DeriveReviewPhase());
    }

    public void Apply(ReviewCompleted @event)
    {
        // A pass still in flight when the cycle concludes belongs to a stream written before
        // lenses existed, where one ReviewCompleted WAS the whole cycle; it is retired here
        // with the cycle's verdict, which for that stream is the verdict it actually returned.
        foreach (ReviewPassSession pass in _inFlightReviewPasses)
        {
            RecordPassResult(
                pass.Lens, pass.TranscriptSessionId, pass.Model, @event.Verdict,
                FindingsOf(null, @event.Verdict), pass.Mode);
        }

        _inFlightReviewPasses.Clear();
        LastReviewVerdict = @event.Verdict;
        // A pre-lens stream keeps the single-lens phase rule it was written under: its one
        // ReviewCompleted is the whole cycle, and re-deriving would send a daemon upgraded
        // mid-review back to top up a lens for a cycle that already concluded.
        SetReviewPhaseAndLatchFullScopeBoundary(
            _cycleHasPassMilestones ? DeriveReviewPhase() : PhaseFor(@event.Verdict));
    }

    public void Apply(ReviewTrackConcluded @event)
    {
        ReviewLens lens = @event.Lens ?? ReviewLens.Unknown;
        _concludedReviewTracks.RemoveAll(track => track.Lens == lens);
        _concludedReviewTracks.Add(new ReviewTrackOutcome(lens, @event.Cycle, @event.Settlement));
        _reviewResiduals.AddRange(@event.Residuals ?? []);
        SetReviewPhaseAndLatchFullScopeBoundary(DeriveReviewPhase());
    }

    /// <summary>See the event's own doc for why this exists: the inverse of <see cref="Apply(ReviewTrackConcluded)"/>, not a replacement of its record.</summary>
    public void Apply(ReviewTrackReactivated @event)
    {
        _concludedReviewTracks.RemoveAll(track => track.Lens == @event.Lens);
        _trackReactivatedAtCycle[@event.Lens] = @event.Cycle;
        ReviewTrackReactivations++;
        SetReviewPhaseAndLatchFullScopeBoundary(DeriveReviewPhase());
    }

    /// <summary>
    /// Sets <see cref="ReviewPhase"/> and, exactly once a <see cref="ReviewMode.Discovery"/> or
    /// <see cref="ReviewMode.FinalFullPass"/> cycle has actually DELIVERED a readable verdict from
    /// every lens it dispatched, latches <see cref="LastFullScopeReviewHeadSha"/> to that cycle's
    /// own <see cref="CycleHeadSha"/> (independent pre-PR review, cycle 1 adversarial finding).
    /// <see cref="ReviewPhase.FixNeeded"/>/<see cref="ReviewPhase.Settling"/> (or, for a pre-lens
    /// stream, <see cref="ReviewPhase.MergeReady"/>) is exactly that confirmation: every other
    /// phase this can land on — <see cref="ReviewPhase.AwaitingVerdict"/> (a lens still owed a
    /// look), <see cref="ReviewPhase.VerdictMissing"/> (a lens answered unreadably) — means at
    /// least one lens never gave this cycle a real verdict, so the boundary must not move past
    /// commits that lens was never confirmed to have read. Latching here rather than at the
    /// cycle's own dispatch is what keeps a park — <see cref="ReviewPhase.VerdictMissing"/> past
    /// its one re-prompt, or a lens never even topped up before the run parked — from narrowing a
    /// later full-scope read past commits no reviewer was ever observed to have read.
    /// <para>
    /// A <see cref="ReviewMode.Discovery"/> cycle only counts when it was itself a full-scope read
    /// — <see cref="CycleSinceSha"/> null (task: a lap reviews only what it changed). A
    /// ReviewFeedback or FailingChecks follow-up's own opening cycle is scoped to the lap's own
    /// change, never the whole branch, so it must not pretend to have read past commits it never
    /// looked at — that is exactly what the mandatory <see cref="ReviewMode.FinalFullPass"/>
    /// backstop exists to still cover; a scoped <see cref="ReviewMode.FinalFullPass"/> itself is the
    /// one exception (its own doc: successive full-scope reads tile the branch with no gap), so it
    /// always counts regardless of its own <see cref="CycleSinceSha"/>.
    /// </para>
    /// </summary>
    private void SetReviewPhaseAndLatchFullScopeBoundary(ReviewPhase phase)
    {
        ReviewPhase = phase;
        bool confirmedFullScopeVerdict = phase is ReviewPhase.FixNeeded or ReviewPhase.Settling or ReviewPhase.MergeReady;
        if (confirmedFullScopeVerdict
            && (CurrentCycleMode == ReviewMode.FinalFullPass
                || (CurrentCycleMode == ReviewMode.Discovery && CycleSinceSha is null)))
        {
            LastFullScopeReviewHeadSha = CycleHeadSha;
        }
    }

    /// <summary>
    /// Routing moves nothing in the loop — the track's own cycle count and pending fixes are
    /// untouched — but it does leave a residual, and this is where that residual is recorded.
    /// It has to be here rather than on the track's conclusion because a routed finding is
    /// routed in whatever cycle it was found, including one that another finding forces the
    /// track to run again; a residual recorded only on a terminal cycle would let a run settle
    /// Clean over a defect it had in fact exported to a draft bug task.
    /// <para>
    /// The scope is stated rather than read off the event because routing already asserts it:
    /// only a finding tagged out-of-scope is ever routable (<c>ReviewFindingScope.IsRoutable</c>),
    /// so an out-of-scope tag is a fact this event carries by its own definition.
    /// </para>
    /// <para>
    /// The disposition, by contrast, is read off the event, because the event exists precisely
    /// to tell the two cases apart: a routing with no <c>DraftTaskId</c> created no draft, and
    /// recording it as Routed would count a bug task nobody can open and print it back to a
    /// human as one more defect safely exported.
    /// </para>
    /// </summary>
    public void Apply(ReviewFindingRouted @event) =>
        _reviewResiduals.Add(new ReviewResidual(
            @event.Lens ?? ReviewLens.Unknown, @event.Cycle, @event.Severity ?? ReviewSeverity.Unknown,
            ReviewFindingScope.OutOfScope,
            @event.DraftTaskId is null
                ? ReviewResidualDisposition.RoutingFailed
                : ReviewResidualDisposition.Routed,
            @event.Location ?? string.Empty));

    public void Apply(ReviewSettled @event)
    {
        // The terminal verdict is MergeReady however the loop got here; the settlement is what
        // says whether a reviewer confirmed it or the gate ended it (log #63).
        LastReviewVerdict = ReviewVerdict.MergeReady;
        ReviewSettlement = @event.Settlement;
        ReviewPhase = ReviewPhase.MergeReady;
        // Every other settle path already sits at UnderReview by the time this fires — its own
        // opening ReviewDispatched set it there — so this is a no-op for them. Composition none
        // is the one path that reaches ReviewSettled having never dispatched a reviewer at all
        // (SettleWithNoReviewAsync), which used to leave State exactly where AgentSessionCompleted
        // last left it: Verifying. RunSupervisor.ResumePipeline branches on State == Verifying to
        // decide whether a resumed run still owes itself a gate run; a daemon restart in the
        // narrow window between this settle and PullRequestOpener.OpenAsync therefore re-ran the
        // full build/test gate over an already-merge-ready tip, discarding the settled result if
        // that rerun failed for any reason a gate can fail for (independent pre-PR review, cycle 1,
        // adversarial finding). Setting it here, unconditionally, is what closes that gap.
        State = RunState.UnderReview;
    }

    public void Apply(ReviewVerdictReprompted @event)
    {
        // SessionId is this leg's artifact identity only; the resumed transcript — and so the
        // pass's identity for anything that follows — continues the ORIGINAL session. The mode is
        // this cycle's own (CurrentCycleMode): a reprompt resumes the same pass under the same
        // cycle, so it never changes what shape that cycle's dispatch took.
        AddInFlightPass(
            @event.Lens ?? ReviewLens.Unknown, @event.SessionId, @event.ResumedSessionId,
            @event.ProcessId, @event.ProcessStartedAt, @event.Model ?? AgentModel.Unknown, CurrentCycleMode);
        VerdictRepromptedCycle = @event.Cycle;
        VerdictRepromptedLens = @event.Lens ?? ReviewLens.Unknown;
        ReviewPhase = ReviewPhase.AwaitingVerdict;
    }

    public void Apply(ReviewFixDispatched @event)
    {
        ReviewFixRuns++;
        // PendingHumanFindings is NOT cleared here: a budget-exhausted fix session redispatches
        // at FixNeeded with nothing else fixed, and must see the same human guidance again
        // rather than falling back to the automated findings file (backlog 40). It is only
        // truly consumed once a fix session actually finishes — see Apply(ReviewFixCompleted).
        ActiveFixSessionId = @event.SessionId;
        ActiveFixProcessId = @event.ProcessId;
        ActiveFixProcessStartedAt = @event.ProcessStartedAt;
        ActiveFixSessionModel = @event.Model ?? AgentModel.Unknown;
        ReviewPhase = ReviewPhase.AwaitingFix;
        // The review-verdict-to-fix boundary's own approval is spent the moment the fix session
        // it bought actually dispatches (task: interactive mode becomes a recorded property of
        // the task).
        InteractiveGateCleared = false;
        // Always true already except the one path that needs it stated: a fix session
        // dispatched to redispatch over a budget park (backlog 40) left State at BudgetParked,
        // and nothing else in this event's normal firing would move it off that.
        State = RunState.UnderReview;

        // Recorded unconditionally, including on a mechanical redispatch (the engine's own
        // ReviewEngine.DispatchFixSessionAsync already reused the prior decision when it decided
        // Escalated/EscalationReason, so overwriting here with the same values a retry carries is
        // harmless) — CurrentCycleFixFindingLocations is read fresh rather than trusted from
        // before this event, but it re-derives the identical set on a same-cycle retry, since
        // nothing about this cycle's own completed passes changed in between.
        LastFixSessionEscalated = @event.Escalated;
        LastFixSessionEscalationReason = @event.EscalationReason;
        // Left untouched on a round dispatched over PendingHumanFindings: that is exactly the
        // set DispatchFixSessionAsync itself refuses to compare against as the CURRENT side
        // (ReviewEngine.cs's own comment on the point) because it "describes what automation was
        // looking at, not what the human said" — installing it as the PREVIOUS side for the next
        // round would defer that same unreliability one round rather than avoid it, escalating a
        // later round against locations no fix session was ever dispatched over.
        if (PendingHumanFindings.IsBlank())
        {
            _lastFixRoundFindingLocations.Clear();
            _lastFixRoundFindingLocations.AddRange(CurrentCycleFixFindingLocations);
        }

        LastFixRoundCycle = @event.Cycle;
        LastFixRoundHumanFindings = PendingHumanFindings;
        _fixDispatchedThisCycle = true;
    }

    public void Apply(ReviewFixCompleted @event)
    {
        ClearActiveFixSession();
        PendingHumanFindings = null;
        PendingHumanFindingsFromInteractiveGate = false;
        // Every fix session is followed by the gates, including the terminal one the severity
        // gate let through: what a settled ending ships unreviewed is the reviewers' reading of
        // those commits, never the build and the tests (log #63). The reverify step is what
        // decides between another cycle and settling, once the gates have actually run.
        ReviewPhase = @event.Outcome == ReviewFixOutcome.Disputed
            ? ReviewPhase.Disputed
            : ReviewPhase.Reverify;
    }

    /// <summary>
    /// The human fixed the cycle's findings by hand and handed the branch back for re-review
    /// (<c>h9k review fixed</c>, task: a human at the wheel takes the fix role herself). This is
    /// the one fix path with no session of its own on either side of it: the commits are already on
    /// the branch, so there is nothing to await and nothing to clear — this event stands in for
    /// <see cref="Apply(ReviewFixDispatched)"/> and <see cref="Apply(ReviewFixCompleted)"/>
    /// together, landing the run on exactly the phase the second of those lands a
    /// <see cref="ReviewFixOutcome.Fixed"/> outcome on.
    /// <para>
    /// What it deliberately does NOT do is everything a dispatched round does about budget and
    /// escalation: <see cref="ReviewFixRuns"/> stays put (it counts sessions the platform
    /// dispatched, and none was), and so does the repeat-findings escalation state
    /// (<see cref="LastFixRoundFindingLocations"/>, <see cref="LastFixRoundCycle"/>,
    /// <see cref="LastFixSessionEscalated"/>) — that comparison is against the most recent
    /// AUTOMATED round by design (see <see cref="LastFixRoundFindingLocations"/>'s own doc), so
    /// installing a human round as its previous side would escalate a later automated round
    /// against locations no fix session was ever dispatched over, the same misreporting
    /// <see cref="Apply(ReviewFixDispatched)"/> already refuses for a human's needs-fixes findings.
    /// </para>
    /// <para>
    /// <see cref="FixDispatchedThisCycle"/>, by contrast, IS set, exactly as a dispatched round
    /// sets it. It is not a budget: it is the record that this cycle's own tip moved after its
    /// reviewers read it, and it is what keeps <c>ReviewEngine.MaySettleReason</c>'s
    /// <c>NothingOwed</c> clause and <c>NeedsFullGateBeforeSettling</c> from letting those commits
    /// reach the remote unread and ungated (Decisions Log #92 — nothing merges on scoped green
    /// alone, whoever wrote the commits). This is the "counts exactly as one opened by a fix
    /// session does" half of the same criterion whose other half is the untouched budget above.
    /// </para>
    /// </summary>
    public void Apply(ReviewHumanFixApplied @event)
    {
        HumanFixRounds++;
        // Consumed on the same terms Apply(ReviewFixCompleted) consumes them: the human answered
        // the boundary these findings were parked at, so they must not ride into whatever the loop
        // dispatches next as though nobody had acted on them.
        PendingHumanFindings = null;
        PendingHumanFindingsFromInteractiveGate = false;
        // The review-verdict-to-fix boundary's own approval is spent by the fix, the same way
        // Apply(ReviewFixDispatched) spends it the moment the session it bought dispatches — so the
        // fix-to-re-review boundary below asks its own question rather than inheriting this one's
        // answer.
        InteractiveGateCleared = false;
        _fixDispatchedThisCycle = true;
        // Reverify, never Disputed: a human cannot dispute their own fix, and Reverify is the
        // existing fix-to-re-review entry point — the gates run over those commits, then a fresh
        // review pass reads them.
        ReviewPhase = ReviewPhase.Reverify;
        // Always true already except the one path that needs it stated, exactly as
        // Apply(ReviewFixDispatched)'s own identical line documents: the park this resolves left
        // State at ReviewParked, and nothing else here would move it off.
        State = RunState.UnderReview;
        ParkedIsInteractiveGate = false;
        ParkedNeedsFixesOffersNoProgress = false;
    }

    public void Apply(PrReviewConformanceDispatched @event)
    {
        PrReviewConformanceSessionId = @event.SessionId;
        PrReviewConformanceProcessId = @event.ProcessId;
        PrReviewConformanceProcessStartedAt = @event.ProcessStartedAt;
        PrReviewConformanceCompleted = false;
        PrReviewConformanceBudgetExhausted = false;
        PrReviewConformanceLaunchHeld = false;
        PrReviewConformanceModel = @event.Model;
        State = RunState.UnderReview;
    }

    public void Apply(PrReviewConformanceCompleted @event) => PrReviewConformanceCompleted = true;

    public void Apply(PrReviewDelivered @event)
    {
        PrReviewDelivered = true;
        State = RunState.UnderReview;
    }

    /// <summary>
    /// Recorded immediately ahead of the <see cref="ReviewParked"/> that actually parks the run
    /// (task: a changes-requested pull-request review from a human becomes a fix lap) — ahead, so
    /// that the park event stays the one thing that moves state, and these positions are already
    /// on the aggregate by the time anything reads the parked run.
    /// </summary>
    public void Apply(ReviewDisagreementParked @event)
    {
        ParkedDisagreements = @event.Disagreements;
        ParkedOnReviewDisagreement = true;
    }

    public void Apply(ReviewParked @event)
    {
        // Captured before the overwrite: State (and, for interactive mode's own gate, ReviewPhase)
        // still hold where the park caught the run, and ReviewPhase also holds which kind of park
        // this is (task: a run rebases its branch onto the current base branch —
        // ParkedFromReviewPhase is what lets a resolved rebase-recovery dispute route to its own
        // resume path below rather than the ordinary FixNeeded one every other UnderReview park
        // already takes).
        ParkedFromState = State;
        ParkedFromReviewPhase = ReviewPhase;
        ParkedNeedsFixesOffersNoProgress = @event.NeedsFixesOffersNoProgress;
        ParkedIsInteractiveGate = @event.IsInteractiveGate;
        ReviewPhase = ReviewPhase.Parked;
        State = RunState.ReviewParked;
    }

    /// <summary>
    /// The bare-proceed sibling of <see cref="Apply(ReviewParkResolved)"/> (task: interactive mode
    /// becomes a recorded property of the task): unlike every branch there, this carries no
    /// verdict to derive a new phase from, so it restores the exact phase and state the park
    /// interrupted rather than deciding a new one. Never reached except from a park where
    /// <see cref="ParkedIsInteractiveGate"/> was true — <c>ReviewProceedCommand</c> is what
    /// enforces that before this event is ever appended.
    /// </summary>
    public void Apply(ReviewBoundaryApproved @event)
    {
        ReviewPhase = ParkedFromReviewPhase;
        // UnderReview, not ParkedFromState, matching RunDetailsProjection.Apply(ReviewBoundaryApproved)
        // and every other resume-the-loop transition below (ReviewParkResolved, ReviewFixDispatched):
        // the resume sweep's own discriminator (RunSupervisor.ResumePipeline) reads UnderReview as
        // "just review, the gates already ran" versus Verifying's "verify then review" — the "build
        // done to review" boundary parks at ParkedFromState == Verifying with its gates already
        // passed, so restoring that literal state here would wrongly re-verify on resume (independent
        // pre-PR review, cycle 1, conformance lens).
        State = RunState.UnderReview;
        InteractiveGateCleared = true;
        ParkedIsInteractiveGate = false;
        ParkedNeedsFixesOffersNoProgress = false;
    }

    public void Apply(ReviewParkResolved @event)
    {
        if (ParkedFromReviewPhase == ReviewPhase.RebaseRecoveryDisputed)
        {
            // A disputed pre-final-pass rebase conflict (task: a run rebases its branch onto the
            // current base branch) needs its own dedicated resume: the ordinary FixNeeded route
            // just below hands PendingHumanFindings to DispatchFixSessionAsync as though it were
            // an ordinary review finding, which is the wrong prompt for "here is how to resolve
            // the conflict." ReviewResolveCommand refuses --merge-ready against this park (nothing
            // has been rebased yet), so a MergeReady verdict is not expected to reach here.
            ReviewPhase = ReviewPhase.RebaseRecoveryNeeded;
            PendingRebaseRecoveryGuidance = @event.Reason;
            ParkedNeedsFixesOffersNoProgress = false;
            State = RunState.UnderReview;
            // A fresh human grant like any other (see the two branches below) — without this, a
            // human's own --needs-fixes retry after this park spends its very next recovery
            // dispatch re-tripping MaxRebaseRecoveryRounds on a count the dispute itself never
            // actually added to landing cleanly or not (independent pre-PR review, cycle 1,
            // conformance lens).
            RebaseRecoveryRounds = 0;
            return;
        }

        // The Settling-gate repair cap's own resolve (task: a pre-final-pass rebase that applies
        // cleanly but breaks the mandatory gate gets a repair lap inside the same run instead of
        // failing it) — a needs-fixes verdict buys exactly one more repair round, carrying the
        // human's text, and deliberately does NOT reset SettlingGateRepairRounds: the task's own
        // criteria define this park's resolve behavior rather than inheriting the rebase-recovery
        // cap's ordinary-fix-session-with-a-fresh-grant shape. A merge-ready verdict is not
        // special-cased here — it falls through to the ordinary MergeReady branch below, the same
        // exit a rebase-recovery cap park offers (unlike the disputed-rebase branch above, which
        // refuses merge-ready outright because nothing has been rebased yet; here the branch is
        // fully built, just failing its own gate).
        if (ParkedFromReviewPhase == ReviewPhase.SettlingGateRepairCapReached
            && @event.Verdict != ReviewVerdict.MergeReady)
        {
            ReviewPhase = ReviewPhase.SettlingGateRepairNeeded;
            PendingSettlingGateRepairGuidance = @event.Reason;
            ParkedNeedsFixesOffersNoProgress = false;
            ParkedDisagreements = [];
            ParkedOnReviewDisagreement = false;
            InteractiveGateCleared = true;
            State = RunState.UnderReview;
            return;
        }

        if (@event.Verdict == ReviewVerdict.MergeReady && ParkedFromState == RunState.Verifying)
        {
            // A thread-dispute park caught this run before the gates (log #62), and interactive
            // mode's own "build done to review" boundary (task: interactive mode becomes a
            // recorded property of the task) parks at this identical ParkedFromState — Verifying,
            // its gates already having passed, cycle 1's review not yet dispatched. Either way, no
            // reviewer has read these commits: the pipeline re-enters where the park interrupted
            // it — Reverify runs the gates (a no-op here; they already passed) then dispatches a
            // review cycle — instead of reporting merge-ready to PullRequestOpener on a verdict no
            // reviewer ever gave. LastReviewVerdict stays untouched for the same reason: nothing
            // reviewed this. An earlier cycle of this same review excluded the interactive gate
            // from this branch so a --merge-ready resolve there would proceed straight to the pull
            // request rather than "re-running the gates and a whole review cycle" — but that
            // exclusion was the actual defect (independent pre-PR review, cycle 1, adversarial
            // lens): it let a single --merge-ready at the very first boundary open a pull request
            // with zero review passes ever dispatched, waiving Decisions Log #92's guarantee with
            // no attestation, the same protection a thread dispute already earns here and
            // --review-stage-composition none only grants after an explicit
            // --accept-reduced-review. A human who wants this boundary to advance without a
            // verdict to give uses the bare proceed verb (h9k review proceed) instead, which
            // dispatches cycle 1's review normally rather than skipping it.
            ReviewPhase = ReviewPhase.Reverify;
        }
        else if (@event.Verdict == ReviewVerdict.MergeReady)
        {
            LastReviewVerdict = ReviewVerdict.MergeReady;
            // A human ending the loop is not a reviewer reading the final tip, so it goes
            // through the settling step like any other ending and records itself as Settled
            // (log #63) rather than borrowing the word Clean.
            _humanEndedTheLoop = true;
            ReviewPhase = ReviewPhase.Settling;
            // The same fresh-grant reset the NeedsFixes branch below gives itself, kept for the
            // same discipline even though it no longer guards the scenario it was written for
            // (independent pre-PR review, cycle 5): ReviewEngine.ReviewPhase.Settling's own settle
            // short-circuit (ReviewEngine.cs, MaySettleReason(run)) takes HumanEndedTheLoop
            // unconditionally, before FinalFullPassCapReached is ever consulted, so a merge-ready
            // resolve now always settles straight through rather than re-parking on the cap — the
            // re-ordering that fixed the cycle-2 finding this reset was originally written against
            // also made the reset itself unreachable-in-effect on this branch (there is nothing
            // left downstream of it for a fresh FinalFullPassRounds to matter to). It stays rather
            // than being dropped: an old value must never carry into whatever the run's next
            // review cycle does, and the moment something changes MaySettleReason's ordering back,
            // this reset is what keeps that path correct without needing to be rediscovered.
            FinalFullPassRounds = 0;
            // RebaseRecoveryRounds is the identical independent bound for the pre-final-pass
            // rebase check (task: a run rebases its branch onto the current base branch) — reset
            // for the same reason FinalFullPassRounds is just above: without it, a run parked on
            // that cap re-parks on the very next check regardless of how many fresh grants the
            // human gives.
            RebaseRecoveryRounds = 0;
            // The identical fresh-grant reset for the Settling-gate repair cap (task: a
            // pre-final-pass rebase that applies cleanly but breaks the mandatory gate gets a
            // repair lap inside the same run instead of failing it) — harmless when this park
            // was not that one, since the count is already 0.
            SettlingGateRepairRounds = 0;
        }
        else
        {
            LastReviewVerdict = ReviewVerdict.NeedsFixes;
            ReviewPhase = ReviewPhase.FixNeeded;
            PendingHumanFindings = @event.Reason;
            // Captured from ParkedIsInteractiveGate here, before it is reset to false a few lines
            // below, and paired with PendingHumanFindings for the rest of its lifecycle: see
            // PendingHumanFindingsFromInteractiveGate's own doc for why reading a shared,
            // unpaired flag at dispatch time instead was the bug.
            PendingHumanFindingsFromInteractiveGate = ParkedIsInteractiveGate;
            // Like a manual pr resolve, the human asking is a fresh grant (log #22): the
            // per-track cycle caps are re-measured from here, so a run parked at its cap does
            // not re-park on the very next cycle. FinalFullPassRounds is an independent bound
            // (its own doc says why) and needs its own reset here for the same reason: without
            // it, a run parked on FinalFullPassCapReached re-parks on the very next check no
            // matter how many fresh grants the human gives, because nothing else ever lowers it.
            ReviewBudgetBaseCycle = ReviewCycle;
            FinalFullPassRounds = 0;
            // Same fresh-grant reset for the pre-final-pass rebase-recovery bound — see the
            // MergeReady branch above.
            RebaseRecoveryRounds = 0;
            // Same fresh-grant reset for the Settling-gate repair cap — see the MergeReady branch
            // above. Only ever meaningful here for a park this branch's own dedicated check above
            // did not already intercept (any park other than SettlingGateRepairCapReached).
            SettlingGateRepairRounds = 0;
        }

        ParkedNeedsFixesOffersNoProgress = false;
        ParkedIsInteractiveGate = false;
        // A drafted reply belongs to the park it was drafted for: the implementer has now said
        // what the reviewer hears (ReviewDisagreementReplyDirected, appended by the resolve just
        // ahead of this), so carrying the draft forward would leave a later reader looking at an
        // unsent proposal that has in fact already been sent or deliberately dropped.
        ParkedDisagreements = [];
        ParkedOnReviewDisagreement = false;
        // A verdict-bearing resolve engages the boundary exactly as a bare proceed would (task:
        // interactive mode becomes a recorded property of the task) — whichever phase this
        // resolution landed the loop on, that phase's own next dispatch must not immediately
        // re-park asking the identical question the human just answered with --merge-ready or
        // --needs-fixes.
        InteractiveGateCleared = true;
        State = RunState.UnderReview;
    }

    /// <summary>
    /// A new cycle starts with no passes: the previous cycle's are history, not state. Mode and
    /// HeadSha are this new cycle's own — read them fresh here rather than trusting a caller's copy,
    /// since a daemon restart replays this from the stream with nothing else in memory.
    /// </summary>
    private void StartCycleIfNew(int cycle, ReviewMode mode, string? headSha, string? sinceSha = null)
    {
        if (cycle == ReviewCycle)
        {
            return;
        }

        ReviewCycle = cycle;
        PriorCycleMode = CurrentCycleMode;
        CurrentCycleMode = mode;
        PriorCycleHeadSha = CycleHeadSha;
        CycleHeadSha = headSha;
        PriorCycleSinceSha = CycleSinceSha;
        CycleSinceSha = sinceSha;

        _inFlightReviewPasses.Clear();
        _completedReviewPasses.Clear();
        _cycleHasPassMilestones = false;
        _fixDispatchedThisCycle = false;
        if (mode == ReviewMode.FinalFullPass)
        {
            FinalFullPassRounds++;
        }
    }

    private void AddInFlightPass(
        ReviewLens lens, Guid sessionId, Guid transcriptSessionId,
        int processId, DateTimeOffset processStartedAt, AgentModel model, ReviewMode mode)
    {
        // One in-flight pass per lens: a redispatch (the daemon died between spawn and
        // record) supersedes its own orphan rather than being waited on twice.
        _inFlightReviewPasses.RemoveAll(pass => pass.Lens == lens);
        _inFlightReviewPasses.Add(new ReviewPassSession(
            lens, sessionId, transcriptSessionId, processId, processStartedAt, model, mode));
    }

    private void RecordPassResult(
        ReviewLens lens, Guid? sessionId, AgentModel model, ReviewVerdict verdict,
        IReadOnlyList<ReviewFindingRecord> findings, ReviewMode mode)
    {
        ReviewPassResult result = new(lens, sessionId, model, verdict, findings, mode);
        int index = _completedReviewPasses.FindIndex(pass => pass.Lens == lens);
        if (index >= 0)
        {
            _completedReviewPasses[index] = result;
        }
        else
        {
            _completedReviewPasses.Add(result);
        }
    }

    /// <summary>
    /// A pass's findings as recorded, or — for a pass written before findings were classified —
    /// the one thing a needs-fixes verdict does tell us: something must be fixed. That
    /// placeholder is ungraded and unplaced on purpose (nothing is invented about it), and it
    /// exists so an older stream still reads as "a fix is owed" rather than as "nothing to do".
    /// </summary>
    private static IReadOnlyList<ReviewFindingRecord> FindingsOf(
        IReadOnlyList<ReviewFindingRecord>? recorded, ReviewVerdict verdict) => recorded switch
    {
        not null => recorded,
        _ when verdict == ReviewVerdict.NeedsFixes =>
        [
            new ReviewFindingRecord(
                ReviewSeverity.Unknown, ReviewFindingScope.Unknown, string.Empty, ReviewFindingDisposition.Fix),
        ],
        _ => [],
    };

    /// <summary>
    /// Where the loop stands once a pass lands or a track concludes (log #59, #63): still
    /// waiting while any active track is in flight or has yet to look at all; parked on a
    /// verdict nobody can read; owing a fix session while any of this cycle's findings is
    /// dispositioned to be fixed; and otherwise finished, with only the account of how it ended
    /// left to write. A cycle one active lens short is not a cycle, whatever the lenses that
    /// did answer said.
    /// </summary>
    private ReviewPhase DeriveReviewPhase()
    {
        if (_inFlightReviewPasses.Count > 0
            || ReviewLens.MissingFrom(CurrentCycleLenses, _completedReviewPasses.Select(pass => pass.Lens)).Count > 0)
        {
            return ReviewPhase.AwaitingVerdict;
        }

        if (_completedReviewPasses.Any(pass => pass.Verdict == ReviewVerdict.Unknown))
        {
            return ReviewPhase.VerdictMissing;
        }

        return PendingFixFindings > 0 ? ReviewPhase.FixNeeded : ReviewPhase.Settling;
    }

    /// <summary>
    /// How the review ended, for the <see cref="Events.ReviewSettled"/> the loop is about to
    /// write. Clean is the narrow claim it sounds like — every track ended on a reviewer that
    /// read the tip and found nothing — so a single outstanding residual, a single settled track,
    /// or a human's own merge-ready resolution is enough to make the ending Settled instead.
    /// <see cref="IsSupersededByCleanReread"/> is what "outstanding" excludes: a FixedUnreviewed
    /// residual a later mandatory <see cref="ReviewMode.FinalFullPass"/> on the same lens went on
    /// to read clean has now had exactly the re-read it was recorded for want of.
    /// </summary>
    public ReviewSettlement DeriveSettlement() =>
        _humanEndedTheLoop
        || _reviewResiduals.Any(residual => !IsSupersededByCleanReread(residual))
        || _concludedReviewTracks.Any(track => track.Settlement == ReviewSettlement.Settled)
            ? ReviewSettlement.Settled
            : ReviewSettlement.Clean;

    /// <summary>
    /// Whether a later cycle's clean conclusion on this residual's own lens supersedes it
    /// (cycle-3 cap-park finding, origin: a run whose severity gate fixed a Medium unreviewed
    /// could never settle Clean even after the mandatory FinalFullPass re-read the same lens and
    /// found nothing, because the append-only residual list still carried the earlier
    /// FixedUnreviewed record). <see cref="_concludedReviewTracks"/> holds one entry per lens —
    /// its OWN latest conclusion, replaced in place by <see cref="Apply(Events.ReviewTrackConcluded)"/>
    /// — so a later Clean entry for this residual's lens is the marker that the re-read actually
    /// happened, consulted here rather than mutating the residual this cycle already wrote to the
    /// stream. Only ever applies to a FixedUnreviewed residual: a Routed or RoutingFailed one
    /// describes a defect this pull request exported rather than fixed, and a RideAlong one a
    /// defect the fix deliberately left behind, so a clean re-read confirms neither away.
    /// <para>
    /// Public so <c>ReviewEngine.SettleAsync</c>'s own cross-cycle already-on-stream matches can
    /// exclude a superseded residual before treating it as proof a place is already accounted
    /// for (independent pre-PR review, cycle 5, both lenses' high finding): a residual this
    /// method excludes contributes nothing to <see cref="PerDefect"/>'s own count, so a raw,
    /// unfiltered match against the stream could let it silently suppress a still-active track's
    /// genuinely outstanding finding at the same place while the tally never counted it at all.
    /// </para>
    /// </summary>
    public bool IsSupersededByCleanReread(ReviewResidual residual) =>
        residual.Disposition == ReviewResidualDisposition.FixedUnreviewed
        && _concludedReviewTracks.Any(track =>
            track.Lens == residual.Lens && track.Settlement == ReviewSettlement.Clean && track.Cycle > residual.Cycle);

    /// <summary>
    /// The residual counts for the <see cref="Events.ReviewSettled"/> the loop is about to
    /// write, per defect rather than per recorded residual (log #63).
    /// <para>
    /// Routing is retried: a routing that failed leaves no draft, so the next cycle to report
    /// the same place tries again, and both records stay on the stream because a stream records
    /// what happened rather than what it wishes had. Counting the records would say "1 routed,
    /// 1 not routed" about one defect that was exported on the second try, and "2 not routed"
    /// about one defect two cycles failed on. So a place that ever routed counts as routed and
    /// nothing else, and repeated records of one place count once.
    /// </para>
    /// <para>
    /// Fixing unreviewed reaches one place twice by two roads of its own: the tracks conclude
    /// separately, so both lenses can end on the same defect, and a single terminal cycle can
    /// state that place in two finding blocks. Neither is two defects, so this count collapses
    /// per place as well.
    /// </para>
    /// <para>
    /// A residual with no location counts on its own every time. It cannot be shown to be
    /// another one, and collapsing unplaced findings together would report several defects as
    /// one on no evidence at all — the same reading the placed dedup gives an unplaced finding.
    /// </para>
    /// <para>
    /// The five counts are deliberately not deduplicated against each other. Only the routing
    /// pair is, because a failed routing and its retry are one export attempted twice. A defect
    /// one track fixed unreviewed and another exported really did meet both ends, and a human
    /// deciding how far to trust this pull request should be told about both — the same reading
    /// applies to a ride-along (Decisions Log #87) and to an <see cref="ReviewResidualDisposition.Unfixed"/>
    /// finding (adversarial review, the routed finding that opened this task): each collapses per
    /// distinct location within itself, exactly as <see cref="ReviewResidualDisposition.RideAlong"/>'s
    /// own doc says, but never against the other four.
    /// </para>
    /// </summary>
    public ReviewResidualTally DeriveResidualTally()
    {
        List<ReviewResidual> routed = PerDefect(ReviewResidualDisposition.Routed);
        List<ReviewResidual> failed = [.. PerDefect(ReviewResidualDisposition.RoutingFailed)
            .Where(residual => !routed.Any(
                done => ReviewFindingLocations.SamePlace(done.Location, residual.Location)))];

        return new ReviewResidualTally(
            PerDefect(ReviewResidualDisposition.FixedUnreviewed).Count,
            routed.Count,
            failed.Count,
            PerDefect(ReviewResidualDisposition.RideAlong).Count,
            PerDefect(ReviewResidualDisposition.Unfixed).Count);
    }

    /// <summary>
    /// The ride-alongs already on this run's stream, named rather than merely counted
    /// (independent pre-PR review, cycle 2, conformance finding): the same deduplicated set
    /// <see cref="DeriveResidualTally"/>'s own <see cref="ReviewResidualTally.RideAlong"/> counts,
    /// but with each one's severity and location so <c>SettleAsync</c> can write what a settling
    /// run's ride-alongs actually are onto <see cref="Events.ReviewSettled"/>, not only how many.
    /// Excludes a track this cycle is force-concluding without ever having read a reviewer's
    /// verdict on it — those are not yet on the stream this reads from, the same gap
    /// <see cref="DeriveResidualTally"/>'s own caller already accounts for separately.
    /// </summary>
    public IReadOnlyList<ReviewResidual> DeriveRideAlongResiduals() => PerDefect(ReviewResidualDisposition.RideAlong);

    /// <summary>
    /// The <see cref="ReviewResidualDisposition.Unfixed"/> residuals already on this run's stream,
    /// named the same way <see cref="DeriveRideAlongResiduals"/> names its own disposition — each
    /// one's severity and location, for <c>SettleAsync</c> to write onto
    /// <see cref="Events.ReviewSettled"/>. Excludes a track this cycle is force-concluding without
    /// ever having read a reviewer's verdict on it, the same gap <see cref="DeriveResidualTally"/>'s
    /// own caller already accounts for separately.
    /// </summary>
    public IReadOnlyList<ReviewResidual> DeriveUnfixedResiduals() => PerDefect(ReviewResidualDisposition.Unfixed);

    /// <summary>
    /// This disposition's residuals with every repeat of a place already seen dropped, and — for
    /// FixedUnreviewed — any residual a later clean re-read on its own lens has already superseded
    /// (<see cref="IsSupersededByCleanReread"/>) left out too, for the same reason
    /// <see cref="DeriveSettlement"/> excludes it: the tally and the settlement it accompanies must
    /// never disagree about what is actually still outstanding.
    /// </summary>
    private List<ReviewResidual> PerDefect(ReviewResidualDisposition disposition)
    {
        List<ReviewResidual> distinct = [];
        foreach (ReviewResidual residual in _reviewResiduals
            .Where(residual => residual.Disposition == disposition && !IsSupersededByCleanReread(residual)))
        {
            if (!distinct.Any(kept => ReviewFindingLocations.SamePlace(kept.Location, residual.Location)))
            {
                distinct.Add(residual);
            }
        }

        return distinct;
    }

    private static ReviewPhase PhaseFor(ReviewVerdict verdict) => verdict switch
    {
        _ when verdict == ReviewVerdict.MergeReady => ReviewPhase.MergeReady,
        _ when verdict == ReviewVerdict.NeedsFixes => ReviewPhase.FixNeeded,
        _ => ReviewPhase.VerdictMissing,
    };

    private void ClearActiveFixSession()
    {
        ActiveFixSessionId = null;
        ActiveFixProcessId = null;
        ActiveFixProcessStartedAt = null;
        ActiveFixSessionModel = AgentModel.Unknown;
    }

    private void ClearActiveRebaseRecoverySession()
    {
        ActiveRebaseRecoverySessionId = null;
        ActiveRebaseRecoveryProcessId = null;
        ActiveRebaseRecoveryProcessStartedAt = null;
        ActiveRebaseRecoveryModel = AgentModel.Unknown;
        ActiveRebaseRecoveryFromCommit = null;
    }

    private void ClearActiveSettlingGateRepairSession()
    {
        ActiveSettlingGateRepairSessionId = null;
        ActiveSettlingGateRepairProcessId = null;
        ActiveSettlingGateRepairProcessStartedAt = null;
        ActiveSettlingGateRepairModel = AgentModel.Unknown;
    }

    // No-op: a logged interaction never changes RunState or any field the write path fences on
    // — it exists here only so this stream replays every event without a gap, the same convention
    // every other Run event upholds (RunDetails.ExternalInteractions is the read model anything
    // that actually needs the history queries).
    public void Apply(ExternalInteractionLogged @event)
    {
    }

    // No-op for the identical reason ExternalInteractionLogged's own Apply above is: delegating
    // this run's own next session to a contractor changes nothing this aggregate fences on — the
    // claim, the assignment, and the worktree/branch are all untouched (design ruling R6). Exists
    // only so this stream replays without a gap; RunDetails.PhaseDelegations is the read model.
    public void Apply(RunPhaseDelegated @event)
    {
    }

    public void Apply(PullRequestOpened @event)
    {
        PullRequestUrl = @event.PullRequestUrl;
        PullRequestNumber = @event.PullRequestNumber;
        State = RunState.AwaitingReview;
    }

    public void Apply(PullRequestUpdated @event)
    {
        PullRequestUrl = @event.PullRequestUrl;
        PullRequestNumber = @event.PullRequestNumber;
        State = RunState.AwaitingReview;
    }

    /// <summary>State-free by design (see the event's own doc): a Failed run stays Failed.</summary>
    public void Apply(PullRequestRecordedOnFailedRun @event)
    {
        PullRequestUrl = @event.PullRequestUrl;
        PullRequestNumber = @event.PullRequestNumber;
    }

    public void Apply(PullRequestChecksFailed @event)
    {
        _failingChecks.Clear();
        _failingChecks.AddRange(@event.FailedChecks);
        State = RunState.ChecksFailing;
    }

    public void Apply(ReviewFeedbackReceived @event)
    {
        UnresolvedReviewThreads = @event.UnresolvedThreadCount;
        State = RunState.ReviewPending;
    }

    // No-op for the same reason Apply(ExternalInteractionLogged) above is: a triage result
    // changes nothing this aggregate fences or gates on — a follow-up that pushes no commit is
    // already the ordinary "no diff to gate" shape. Exists only so this stream replays without a
    // gap; RunDetails.ReviewThreadOutcomes is the read model.
    public void Apply(ReviewThreadsTriaged @event)
    {
    }

    public void Apply(ReviewErrored @event)
    {
        ErroredReviewUrl = @event.ReviewUrl;
        State = RunState.ReviewPending;
    }

    // Informational only: the phase line reads all three fields, but the run's own state
    // machine never branches on them — the events that do (ReviewFeedbackReceived,
    // ReviewErrored) already carry their own state transitions.
    public void Apply(ExternalReviewObserved @event)
    {
        ExternalReviewState = @event.State;
        ExternalReviewThreadCount = @event.ThreadCount;
        ExternalReviewChecksPending = @event.ChecksPending;
    }

    public void Apply(PullRequestConflictObserved @event)
    {
        State = RunState.Conflicting;
    }

    // Informational only, exactly like Apply(ExternalReviewObserved) above: a clean success
    // leaves State untouched (still AwaitingReview) so the very next sweep re-inspects the pushed
    // head, and a fallback is followed in the same transaction by PullRequestConflictObserved,
    // which is what actually moves State to Conflicting.
    public void Apply(PullRequestMechanicalRebaseAttempted @event)
    {
        LastMechanicalRebaseSucceeded = @event.Succeeded;
        LastMechanicalRebaseDetail = @event.Detail;
        LastMechanicalRebasePushedCommit = @event.PushedCommit;
        LastMechanicalRebaseAt = @event.AttemptedAt;
    }

    // Informational only, exactly like Apply(PullRequestMechanicalRebaseAttempted) above. The
    // recorded base changes only on a SUCCESSFUL retarget: a failed attempt left the pull request
    // aimed where it already was, and saying otherwise here would take this run off the merge bar's
    // un-retargeted guard on the strength of an attempt that failed. It is cleared to blank rather
    // than set to ToBase, which is the same value: blank is what BaseBranch means by "the project's
    // own base branch", and holding that invariant is what lets a reader with no project in hand
    // tell a stacked run from an ordinary one (RunDetails.StackedOnBranch's own doc).
    public void Apply(StackedPullRequestRetargeted @event)
    {
        LastStackedRetargetSucceeded = @event.Succeeded;
        LastStackedRetargetDetail = @event.Detail;
        LastStackedRetargetAt = @event.RetargetedAt;
        if (@event.Succeeded)
        {
            BaseBranch = string.Empty;
        }
    }

    // Informational only, exactly like Apply(PullRequestMechanicalRebaseAttempted) above: State
    // is untouched either way. ReviewPhase is untouched by a clean or no-op outcome too — but not
    // because the mandatory gate and pass are always about to run next (independent pre-PR
    // review, cycle 1, both lenses: the ordinary "nothing owed" settle path proved that
    // assumption false), which is exactly why a real rebase also has to raise
    // PreFinalPassRebaseAwaitingGate below rather than trusting whatever runs next to notice it.
    public void Apply(RunRebasedOntoBase @event)
    {
        // Mirrors RunDetailsProjection.Apply(RunRebasedOntoBase)'s own guard (independent pre-PR
        // review, cycle 1, adversarial lens): the pre-final-pass check re-runs on every Settling
        // entry, so a real rebase or a recovery is routinely followed, moments later, by a no-op
        // re-check that finds nothing left to do — that later no-op carries no new information and
        // must not clobber the meaningful outcome already on record here, since this aggregate,
        // not only the projection, is what a future consumer (a decider guard, a new projection)
        // would reach for first. RebaseRecoveryRounds and PreFinalPassRebaseAwaitingGate/Review
        // below are deliberately outside this guard: each of those observes every event on its own
        // terms rather than only the last meaningful one.
        bool isTrailingNoOpAfterRealRebase = @event.WasNoOp
            && LastPreFinalPassRebaseAt is not null
            && LastPreFinalPassRebaseWasNoOp == false;
        if (!isTrailingNoOpAfterRealRebase)
        {
            LastPreFinalPassRebaseWasNoOp = @event.WasNoOp;
            LastPreFinalPassRebaseRecovered = @event.RecoveredByAgentSession;
            LastPreFinalPassRebaseFromCommit = @event.RebasedFromCommit;
            LastPreFinalPassRebaseOntoCommit = @event.RebasedOntoCommit;
            LastPreFinalPassRebaseDetail = @event.Detail;
            LastPreFinalPassRebaseAt = @event.RebasedAt;
        }

        // A real rebase moves this branch's fork point, so the recorded one stops being true of it
        // (independent pre-PR review, cycle 1, adversarial lens): a stale BaseCommit names a commit
        // the branch may no longer contain, and a replay dispatched from a stale upstream re-applies
        // commits it was supposed to drop. Only a non-no-op event moves anything — a no-op means
        // origin's base was already contained, so the fork point is exactly where it was — and only
        // an actual commit is written: RebasedOntoCommit carries the literal "unknown" sentinel when
        // the read failed (ReviewEngine.ResolveObservedOntoCommitAsync), which is an admitted gap,
        // never a commit to record as this branch's fork point.
        if (!@event.WasNoOp && @event.OntoCommitObserved)
        {
            BaseCommit = @event.RebasedOntoCommit;
        }

        // RebaseRecoveryRounds' own doc promises "in a row without ever landing cleanly", and a
        // confirmed no-op is the only fact here git itself observed rather than a session's own
        // claim: it means EnsureRebasedBeforeFinalPassAsync freshly compared origin/<base>'s tip
        // against this branch's own merge-base and found nothing left to rebase, which is only
        // ever true once a prior conflict is genuinely behind this branch (independent pre-PR
        // review, cycle 1, adversarial lens — reset instead on every non-no-op event, as first
        // tried here, means a recovery session's own unverified "RESOLUTION: fixed" claim resets
        // the count on the very dispatch it should have been counted against, so a session that
        // repeats the identical false claim every round never trips MaxRebaseRecoveryRounds at
        // all — caught by A_rebase_recovery_session_that_never_actually_resolves_parks_once_the_round_cap_is_spent,
        // which exists specifically to prove that cap holds). Reset the same way a human's fresh
        // grant already does in Apply(ReviewParkResolved), so a run whose conflicts genuinely keep
        // resolving does not creep toward the cap over defects that never actually recur.
        if (@event.WasNoOp)
        {
            RebaseRecoveryRounds = 0;
        }

        // A real (non-no-op) rebase landing — whether git applied it cleanly on its own or a
        // recovery session merely claimed to — is exactly when this branch's own commits have not
        // been gated at their new, possibly-rebased position (see PreFinalPassRebaseAwaitingGate's
        // own doc): worth an extra full gate to confirm even when the claim behind it turns out to
        // be false, since that gate is what would catch the false claim in the first place. A
        // no-op rebase that still committed a Decisions Log renumbering earns the same gate for
        // the same reason — the renumbering commit moved this branch's tip too — without needing
        // WasNoOp itself to lie about whether origin's base actually moved (independent pre-PR
        // review, cycle 5, adversarial lens: the earlier `wasNoOp: !renumberCommitted` shape broke
        // the trailing-no-op guard just below, which reads WasNoOp for its own, unrelated purpose).
        //
        // FromRealRebase carries an earlier still-ungated real rebase forward across exactly this
        // renumbering-only no-op, rather than resetting to false the way a bare `!WasNoOp` would
        // (independent pre-PR review, cycle 5, both lenses): a recovered rebase's own re-entry
        // lands here twice in a row — once as the real, non-no-op rebase itself, then again as the
        // mechanical renumbering-only no-op DecisionsLogRenumberer commits on the very next
        // Settling entry, before any gate has run over either landing — so resetting on the second
        // landing would make a recovered rebase permanently ineligible for the Settling-gate
        // repair lap. Once an earlier real rebase HAS been gated clean, PreFinalPassRebaseAwaitingGate
        // is already false when a later, unrelated renumbering-only no-op arrives, so the carry-
        // forward term is false and this correctly resolves to `!WasNoOp` alone.
        if (!@event.WasNoOp || @event.DecisionsLogRenumbered)
        {
            PreFinalPassRebaseAwaitingGateFromRealRebase = !@event.WasNoOp
                || (PreFinalPassRebaseAwaitingGate && PreFinalPassRebaseAwaitingGateFromRealRebase);
            PreFinalPassRebaseAwaitingGate = true;
        }

        // Only a recovery session's own judgment call earns this (see PreFinalPassRebaseAwaitingReview's
        // own doc): a clean git apply never sets it, by the identical 2026-09-04 ruling
        // PreFinalPassRebaseAwaitingGate's own comment above already rests on.
        if (@event.RecoveredByAgentSession)
        {
            PreFinalPassRebaseAwaitingReview = true;
        }
    }

    public void Apply(PreFinalPassRebaseRecoveryDispatched @event)
    {
        ActiveRebaseRecoverySessionId = @event.SessionId;
        ActiveRebaseRecoveryProcessId = @event.ProcessId;
        ActiveRebaseRecoveryProcessStartedAt = @event.ProcessStartedAt;
        ActiveRebaseRecoveryModel = @event.Model ?? AgentModel.Unknown;
        ActiveRebaseRecoveryFromCommit = @event.RebasedFromCommit;
        ReviewPhase = ReviewPhase.AwaitingRebaseRecovery;
        State = RunState.UnderReview;
        RebaseRecoveryRounds++;
    }

    public void Apply(PreFinalPassRebaseRecoveryCompleted @event)
    {
        ClearActiveRebaseRecoverySession();
        PendingRebaseRecoveryGuidance = null;
        // Resolved (or undeclared — treated optimistically, the same as an ordinary fix session's
        // Unknown outcome) returns straight to Settling: the loop's own next check re-reads the
        // worktree, finds the base already merged, and proceeds to the mandatory gate and pass
        // exactly as a clean rebase would have. Disputed parks instead — see
        // ReviewPhase.RebaseRecoveryDisputed's own doc for why this is a phase of its own rather
        // than a reuse of Disputed.
        ReviewPhase = @event.Outcome == ReviewFixOutcome.Disputed
            ? ReviewPhase.RebaseRecoveryDisputed
            : ReviewPhase.Settling;
    }

    public void Apply(SettlingGateRepairDispatched @event)
    {
        ActiveSettlingGateRepairSessionId = @event.SessionId;
        ActiveSettlingGateRepairProcessId = @event.ProcessId;
        ActiveSettlingGateRepairProcessStartedAt = @event.ProcessStartedAt;
        ActiveSettlingGateRepairModel = @event.Model ?? AgentModel.Unknown;
        LastSettlingGateRepairOutput = @event.GateOutput;
        ReviewPhase = ReviewPhase.AwaitingSettlingGateRepair;
        State = RunState.UnderReview;
        SettlingGateRepairRounds++;
    }

    public void Apply(SettlingGateRepairCompleted @event)
    {
        ClearActiveSettlingGateRepairSession();
        PendingSettlingGateRepairGuidance = null;
        // Always back to Settling, whatever the session claimed: unlike a rebase conflict, there is
        // no session-declared outcome to trust or dispute here — only the mandatory gate the next
        // Settling entry runs again is the judge of whether this round actually fixed anything (see
        // this event's own doc).
        ReviewPhase = ReviewPhase.Settling;
        // A repair session's own fix is exactly the same kind of unreviewed judgment call a
        // rebase-recovery session's own conflict resolution is (independent pre-PR review, cycle 1,
        // adversarial lens) — without this, a clean Settling gate pass right after this event lets
        // MaySettleReason's NothingOwed clause settle the run straight through, and whatever the
        // repair session wrote (rewritten call sites, a deleted or skipped test) reaches the pull
        // request without a fresh-context reviewer ever reading it. Mirrors
        // PreFinalPassRebaseAwaitingReview's own doc: cleared the moment the next review pass is
        // actually dispatched, never by the gate passing on its own.
        PreFinalPassRebaseAwaitingReview = true;
    }

    public void Apply(SettlingGateRepairCapReached @event)
    {
        ReviewPhase = ReviewPhase.SettlingGateRepairCapReached;
        // The park's own failure output, not whatever an earlier round's SettlingGateRepairDispatched
        // last left here (see the event's own doc) — the bought round SettlingGateRepairNeeded
        // dispatches next reads this property back to build its prompt, and must see the failure the
        // park itself reported, not a fixed-and-gone one from a prior round.
        LastSettlingGateRepairOutput = @event.GateOutput;
    }

    public void Apply(ReviewRerequested @event)
    {
        ReviewRerequestCount++;
        _requestedReviewerLogins.Add(@event.Reviewer);
    }

    // No state change: a countersign request is a question asked, not a finding received,
    // so the run stays AwaitingReview while the monitor watches for the answer.
    public void Apply(ReviewRerequestedAfterFixes @event)
    {
        ReviewRerequestsAfterFixes++;
        _requestedReviewerLogins.AddRange(@event.Reviewers);
    }

    public void Apply(CloseoutParked @event) => State = RunState.CloseoutParked;

    public void Apply(CloseoutBudgetGranted @event) => HumanGrantedAt = @event.GrantedAt;

    public void Apply(PullRequestMerged @event) => PullRequestMergedAt = @event.MergedAt;

    public void Apply(PullRequestClosed @event) => State = RunState.Failed;

    public void Apply(RunHandoffRecorded @event)
    {
        HandoffOutcome = @event.Outcome ?? HandoffOutcome.Unknown;
        HandoffSummary = @event.Summary;
    }

    // The synthesis session is bookkeeping on the dependent's own run: it neither moves the
    // run state nor gates anything, so the aggregate records only that it happened.
    public void Apply(ContextSynthesisDispatched @event) => ContextSynthesisSessions++;

    public void Apply(ContextSynthesisCompleted @event) => ContextSynthesized = @event.Synthesized;

    public void Apply(RunCompleted @event) => State = RunState.Completed;

    /// <summary>
    /// External and clock-recoverable wherever it lands (backlog 40) — the primary session,
    /// a review pass, or the fix session. The primary-session case needs nothing else: its
    /// resume is <c>TokenBudgetRetryEngine</c> replaying the same session it always did. A
    /// review pass or the fix session dies with the run loop mid-cycle, though, and the
    /// process that carried it is gone by the time this lands — so the exhausted work is
    /// cleared here rather than left to be "resumed": DispatchMissingPassesAsync tops up a
    /// cleared review pass exactly as it already does for a daemon that died between two
    /// spawns, and a cleared fix session re-enters at FixNeeded to redispatch fresh over the
    /// same findings — the automated findings file, or a human's PendingHumanFindings, whichever
    /// the exhausted session was actually working from (PendingHumanFindings survives here
    /// untouched; it is only cleared once a fix session actually finishes). Neither loses the
    /// run or the task; only the one session's own progress goes with it.
    /// </summary>
    public void Apply(RunBudgetExhausted @event)
    {
        State = RunState.BudgetParked;
        switch (ReviewPhase)
        {
            case ReviewPhase.AwaitingVerdict:
                _inFlightReviewPasses.Clear();
                break;
            case ReviewPhase.AwaitingFix:
                ClearActiveFixSession();
                ReviewPhase = ReviewPhase.FixNeeded;
                break;
            case ReviewPhase.AwaitingRebaseRecovery:
                // Mirrors the AwaitingFix case above (task: a run rebases its branch onto the
                // current base branch): the exhausted recovery session's process is gone, so the
                // leg is cleared here rather than left for the budget retry sweep to "resume" a
                // dead process. RebaseRecoveryNeeded re-enters DispatchRebaseRecoverySessionAsync
                // fresh — PendingRebaseRecoveryGuidance is left untouched here, mirroring
                // PendingHumanFindings' own doc just above, so a retry after a human's own
                // --needs-fixes resolution still carries their guidance into the fresh attempt;
                // it reads as no guidance only when this exhaustion's own attempt genuinely never
                // had any (an ordinary conflict's own first attempt, which never set the field).
                ClearActiveRebaseRecoverySession();
                ReviewPhase = ReviewPhase.RebaseRecoveryNeeded;
                break;
            case ReviewPhase.AwaitingSettlingGateRepair:
                // Mirrors the AwaitingRebaseRecovery case above (task: a pre-final-pass rebase
                // that applies cleanly but breaks the mandatory gate gets a repair lap inside the
                // same run instead of failing it) — the exhausted session's process is gone, so
                // SettlingGateRepairNeeded re-enters the dispatch fresh, carrying whatever gate
                // output and human guidance this exhaustion's own attempt already had (both left
                // untouched here for the identical reason PendingRebaseRecoveryGuidance's own case
                // just above leaves its own guidance untouched).
                // SettlingGateRepairRounds is given back here (PR #316 review): Apply(SettlingGateRepairDispatched)
                // already counted this exhausted attempt as a round, and the redispatch below counts
                // it again — without the give-back, a session-level exhaustion (nothing to do with
                // whether the repair actually fixed anything) would silently spend a round the
                // acceptance criteria's own "one fix session over the gate output followed by the
                // mandatory gate again" never intended it to spend.
                SettlingGateRepairRounds--;
                ClearActiveSettlingGateRepairSession();
                ReviewPhase = ReviewPhase.SettlingGateRepairNeeded;
                break;
        }

        if (PrReviewConformanceSessionId is not null && !PrReviewConformanceCompleted)
        {
            PrReviewConformanceBudgetExhausted = true;
        }
    }

    /// <summary>
    /// Mirrors <see cref="Apply(RunBudgetExhausted)"/> exactly, including its choice to clear
    /// every in-flight review pass rather than only the one that hit the shape (task: a session
    /// that exits at once with no work done is treated as the node failing to launch sessions):
    /// a node-wide launch hold, like a budget park, can sit for as long as a human takes to
    /// notice, and nobody would ever read a sibling lens's verdict either while the whole run
    /// waits on it. <see cref="RunState.LaunchHeld"/> is the only difference from the budget
    /// park's own State — the node-wide hold's own probe is what resumes this, never the hourly
    /// budget-retry sweep, and never this run's own SessionErrorRetryBackoff timer.
    /// </summary>
    public void Apply(RunLaunchHeld @event)
    {
        State = RunState.LaunchHeld;
        switch (ReviewPhase)
        {
            case ReviewPhase.AwaitingVerdict:
                _inFlightReviewPasses.Clear();
                break;
            case ReviewPhase.AwaitingFix:
                ClearActiveFixSession();
                ReviewPhase = ReviewPhase.FixNeeded;
                break;
            case ReviewPhase.AwaitingRebaseRecovery:
                ClearActiveRebaseRecoverySession();
                ReviewPhase = ReviewPhase.RebaseRecoveryNeeded;
                break;
            case ReviewPhase.AwaitingSettlingGateRepair:
                // Mirrors Apply(RunBudgetExhausted)'s identical case: the give-back matters here
                // too, since Apply(SettlingGateRepairDispatched) already counted the held attempt
                // as a round and the redispatch below counts it again.
                SettlingGateRepairRounds--;
                ClearActiveSettlingGateRepairSession();
                ReviewPhase = ReviewPhase.SettlingGateRepairNeeded;
                break;
        }

        // Mirrors Apply(RunBudgetExhausted)'s identical flag for the pr-review task type's own
        // conformance lens, which touches none of the ReviewPhase cases above (PrReviewEngine's
        // class doc comment explains why): without this, a resumed run would read the held
        // session's own leftover stream file as still live and never redispatch a fresh one.
        if (PrReviewConformanceSessionId is not null && !PrReviewConformanceCompleted)
        {
            PrReviewConformanceLaunchHeld = true;
        }
    }

    /// <summary>
    /// Records the one retry a leg/cycle/lens combination gets (task: a session that reports an
    /// error result is retried once in place). Deliberately narrower than
    /// <see cref="Apply(RunBudgetExhausted)"/>: that clears every in-flight review pass because
    /// a park may sit for an hour and nobody would ever read a sibling's verdict either, while
    /// this retry is a short in-place backoff, so a review-pass leg removes only ITS OWN
    /// in-flight entry (<see cref="ReviewPass"/> keeps a still-running sibling lens exactly as it
    /// was — the run engine redispatches the missing lens the same way an ordinary crash-recovery
    /// top-up already does) and a fix leg clears the active fix session so the loop's own
    /// FixNeeded phase redispatches it fresh over the same findings.
    /// </summary>
    public void Apply(RunSessionErrorRetried @event)
    {
        _errorRetriedLegs.Add(ErrorRetryLegKey(@event.Leg, @event.Cycle, @event.Lens));
        SessionErrorRetryCount++;

        if (@event.Leg == RunSessionLeg.ReviewPass)
        {
            ReviewLens lens = @event.Lens ?? ReviewLens.Unknown;
            _inFlightReviewPasses.RemoveAll(pass => pass.Lens == lens);
        }
        else if (@event.Leg == RunSessionLeg.Fix)
        {
            ClearActiveFixSession();
            ReviewPhase = ReviewPhase.FixNeeded;
        }
        else if (@event.Leg == RunSessionLeg.RebaseRecovery)
        {
            ClearActiveRebaseRecoverySession();
            ReviewPhase = ReviewPhase.RebaseRecoveryNeeded;
        }
        else if (@event.Leg == RunSessionLeg.SettlingGateRepair)
        {
            // The identical give-back Apply(RunBudgetExhausted)'s own AwaitingSettlingGateRepair
            // case documents: this errored attempt already counted as a round, and the retry
            // dispatch below counts it again, so an error result — not a genuine gate failure —
            // never costs a round on its own.
            SettlingGateRepairRounds--;
            ClearActiveSettlingGateRepairSession();
            ReviewPhase = ReviewPhase.SettlingGateRepairNeeded;
        }
    }

    /// <summary>Whether <paramref name="leg"/>/<paramref name="cycle"/>/<paramref name="lens"/> has already spent its one error-result retry — a second occurrence here fails the run.</summary>
    public bool HasRetriedSessionError(RunSessionLeg leg, int? cycle, ReviewLens? lens) =>
        _errorRetriedLegs.Contains(ErrorRetryLegKey(leg, cycle, lens));

    private static string ErrorRetryLegKey(RunSessionLeg leg, int? cycle, ReviewLens? lens) =>
        $"{leg.Value}:{cycle?.ToString() ?? "-"}:{lens?.Value ?? "-"}";

    public void Apply(RunFailed @event) => State = RunState.Failed;

    public void Apply(RunKilled @event) => State = RunState.Killed;

    public void Apply(RunSuperseded @event) => State = RunState.Superseded;

    public void Apply(InteractiveSessionStarted @event)
    {
        InteractiveClaudeSessionId = @event.ClaudeSessionId;
        InteractiveSessionCount++;
        State = RunState.Running;
    }

    // No state change: the run stays wherever the interactive session left it (Claimed on the
    // task, Running here) until an explicit h9k task deliver/release/handback moves it on —
    // closing the terminal is normal, not an ending (AGENTS.md).
    public void Apply(InteractiveSessionEnded @event)
    {
        if (@event.InputTokens is { } input)
        {
            InputTokens += input;
        }

        if (@event.OutputTokens is { } output)
        {
            OutputTokens += output;
        }

        if (@event.CostUsd is { } cost)
        {
            CostUsd = (CostUsd ?? 0m) + cost;
        }
    }
}
