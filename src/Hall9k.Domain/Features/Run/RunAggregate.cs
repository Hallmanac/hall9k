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
    public ExecutorMode ExecutorMode { get; private set; } = ExecutorMode.Unknown;
    /// <summary>The model the build session was spawned on, as resolved at dispatch (log #33). Unknown on streams written before the chain existed.</summary>
    public AgentModel Model { get; private set; } = AgentModel.Unknown;
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


    /// The pre-PR review loop (log #24): which round of review the run is on, from 1. Every
    /// still-active track shares it — tracks only ever advance together, because the only thing
    /// that advances one is a fix session and gates that every live track then re-reads (log
    /// #63). A track's OWN cycle count is therefore the cycle it last ran at, which is this
    /// number while it is active and frozen at its conclusion once it is not.
    /// </summary>
    public int ReviewCycle { get; private set; }
    /// <summary>Automatic fix sessions dispatched so far. A count for the record; the loop's bounds are the per-track cycle caps (log #63).</summary>
    public int ReviewFixRuns { get; private set; }
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
        [.. ReviewLens.CycleLenses.Where(lens => !_concludedReviewTracks.Any(track => track.Lens.Covers(lens)))];

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
        CurrentCycleMode == ReviewMode.FinalFullPass ? ReviewLens.CycleLenses : ActiveReviewLenses;

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
        ExecutorMode = @event.ExecutorMode;
        Model = @event.Model ?? AgentModel.Unknown;
        DispatchedAt = @event.DispatchedAt;
        IsFollowUp = @event.IsFollowUp;
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

    public void Apply(AgentSessionCompleted @event) => State = RunState.Verifying;

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
    }

    public void Apply(GateRetried @event) => GateRetries++;

    public void Apply(ReviewDispatched @event)
    {
        StartCycleIfNew(@event.Cycle, @event.Mode ?? ReviewMode.Discovery, @event.HeadSha);
        AddInFlightPass(
            @event.Lens ?? ReviewLens.Unknown, @event.SessionId, @event.SessionId,
            @event.ProcessId, @event.ProcessStartedAt, @event.Model ?? AgentModel.Unknown, CurrentCycleMode);
        ReviewPhase = ReviewPhase.AwaitingVerdict;
        State = RunState.UnderReview;
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
        ReviewPhase = DeriveReviewPhase();
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
        ReviewPhase = _cycleHasPassMilestones ? DeriveReviewPhase() : PhaseFor(@event.Verdict);
    }

    public void Apply(ReviewTrackConcluded @event)
    {
        ReviewLens lens = @event.Lens ?? ReviewLens.Unknown;
        _concludedReviewTracks.RemoveAll(track => track.Lens == lens);
        _concludedReviewTracks.Add(new ReviewTrackOutcome(lens, @event.Cycle, @event.Settlement));
        _reviewResiduals.AddRange(@event.Residuals ?? []);
        ReviewPhase = DeriveReviewPhase();
    }

    /// <summary>See the event's own doc for why this exists: the inverse of <see cref="Apply(ReviewTrackConcluded)"/>, not a replacement of its record.</summary>
    public void Apply(ReviewTrackReactivated @event)
    {
        _concludedReviewTracks.RemoveAll(track => track.Lens == @event.Lens);
        _trackReactivatedAtCycle[@event.Lens] = @event.Cycle;
        ReviewTrackReactivations++;
        ReviewPhase = DeriveReviewPhase();
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
        // Every fix session is followed by the gates, including the terminal one the severity
        // gate let through: what a settled ending ships unreviewed is the reviewers' reading of
        // those commits, never the build and the tests (log #63). The reverify step is what
        // decides between another cycle and settling, once the gates have actually run.
        ReviewPhase = @event.Outcome == ReviewFixOutcome.Disputed
            ? ReviewPhase.Disputed
            : ReviewPhase.Reverify;
    }

    public void Apply(ReviewParked @event)
    {
        // Captured before the overwrite: State still holds where the park caught the run.
        ParkedFromState = State;
        ReviewPhase = ReviewPhase.Parked;
        State = RunState.ReviewParked;
    }

    public void Apply(ReviewParkResolved @event)
    {
        if (@event.Verdict == ReviewVerdict.MergeReady && ParkedFromState == RunState.Verifying)
        {
            // A thread-dispute park caught this run before the gates (log #62). The human
            // decided the disputed thread, not the diff: no gate has run over these commits
            // and no reviewer has read them, so the pipeline re-enters where the park
            // interrupted it — Reverify runs the gates, then a review cycle — instead of
            // reporting merge-ready to PullRequestOpener on a verdict nobody gave.
            // LastReviewVerdict stays untouched for the same reason: nothing reviewed this.
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
        }
        else
        {
            LastReviewVerdict = ReviewVerdict.NeedsFixes;
            ReviewPhase = ReviewPhase.FixNeeded;
            PendingHumanFindings = @event.Reason;
            // Like a manual pr resolve, the human asking is a fresh grant (log #22): the
            // per-track cycle caps are re-measured from here, so a run parked at its cap does
            // not re-park on the very next cycle. FinalFullPassRounds is an independent bound
            // (its own doc says why) and needs its own reset here for the same reason: without
            // it, a run parked on FinalFullPassCapReached re-parks on the very next check no
            // matter how many fresh grants the human gives, because nothing else ever lowers it.
            ReviewBudgetBaseCycle = ReviewCycle;
            FinalFullPassRounds = 0;
        }

        State = RunState.UnderReview;
    }

    /// <summary>
    /// A new cycle starts with no passes: the previous cycle's are history, not state. Mode and
    /// HeadSha are this new cycle's own — read them fresh here rather than trusting a caller's copy,
    /// since a daemon restart replays this from the stream with nothing else in memory.
    /// </summary>
    private void StartCycleIfNew(int cycle, ReviewMode mode, string? headSha)
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
    /// </summary>
    private bool IsSupersededByCleanReread(ReviewResidual residual) =>
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
    /// The four counts are deliberately not deduplicated against each other. Only the routing
    /// pair is, because a failed routing and its retry are one export attempted twice. A defect
    /// one track fixed unreviewed and another exported really did meet both ends, and a human
    /// deciding how far to trust this pull request should be told about both — the same reading
    /// applies to a ride-along (Decisions Log #87): it collapses per distinct location within
    /// itself, exactly as <see cref="ReviewResidualDisposition.RideAlong"/>'s own doc says, but
    /// never against the other three.
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
            PerDefect(ReviewResidualDisposition.RideAlong).Count);
    }

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
        }
    }

    public void Apply(RunFailed @event) => State = RunState.Failed;

    public void Apply(RunKilled @event) => State = RunState.Killed;

    public void Apply(RunSuperseded @event) => State = RunState.Superseded;
}
