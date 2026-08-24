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
    /// Where this run's artifacts live, as recorded on <see cref="RunDispatched"/>. A stream
    /// written before the field existed falls back to <see cref="RunPaths.GlobalDirectory"/> —
    /// the same place its files have always actually been — so every reader can use this
    /// property directly with no fallback of its own to remember.
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

    /// <summary>When a human last granted this run's task a fresh closeout budget (h9k pr resolve, Decisions Log #77, backlog 45); null until one lands.</summary>
    public DateTimeOffset? HumanGrantedAt { get; private set; }

    /// <summary>Errored-review re-requests issued for this run; adds to the task's CloseoutAttempts against the shared budget.</summary>
    public int ReviewRerequestCount { get; private set; }

    /// <summary>
    /// Countersign re-requests issued after this run pushed its fixes (Decisions Log #62) — at
    /// most one per run, so this reads as "has this run already asked".
    /// </summary>
    public int ReviewRerequestsAfterFixes { get; private set; }


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
    /// The review tracks that have finished, in the order they finished (log #63). A concluded
    /// track is never dispatched again and is deliberately never reawakened by the other
    /// track's fix sessions.
    /// </summary>
    public IReadOnlyList<ReviewTrackOutcome> ConcludedReviewTracks => _concludedReviewTracks;

    /// <summary>
    /// The tracks a cycle still dispatches: every opening lens that has not concluded (log #63).
    /// Empty means the loop is finished looking.
    /// </summary>
    public IReadOnlyList<ReviewLens> ActiveReviewLenses =>
        [.. ReviewLens.CycleLenses.Where(lens => !_concludedReviewTracks.Any(track => track.Lens.Covers(lens)))];

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

    /// <summary>This cycle's findings that are still owed a fix session — the loop's "is there anything to fix" (log #63).</summary>
    public int PendingFixFindings =>
        _completedReviewPasses.Sum(pass =>
            pass.Findings.Count(finding => finding.Disposition == ReviewFindingDisposition.Fix));

    /// <summary>Whether this cycle recorded per-pass milestones. False for a pre-lens stream, whose one ReviewCompleted IS the cycle.</summary>
    private bool _cycleHasPassMilestones;

    /// <summary>Whether a human's merge-ready park resolution is what ended the loop, rather than a clean reviewer.</summary>
    private bool _humanEndedTheLoop;

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
    /// <summary>The highest cycle whose verdict re-prompt was already spent (0 = never). One re-prompt per CYCLE, then park.</summary>
    public int VerdictRepromptedCycle { get; private set; }
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
        StartCycleIfNew(@event.Cycle);
        AddInFlightPass(
            @event.Lens ?? ReviewLens.Unknown, @event.SessionId, @event.SessionId,
            @event.ProcessId, @event.ProcessStartedAt, @event.Model ?? AgentModel.Unknown);
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
            FindingsOf(@event.Findings, @event.Verdict));
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
                FindingsOf(null, @event.Verdict));
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
        // pass's identity for anything that follows — continues the ORIGINAL session.
        AddInFlightPass(
            @event.Lens ?? ReviewLens.Unknown, @event.SessionId, @event.ResumedSessionId,
            @event.ProcessId, @event.ProcessStartedAt, @event.Model ?? AgentModel.Unknown);
        VerdictRepromptedCycle = @event.Cycle;
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
            // not re-park on the very next cycle.
            ReviewBudgetBaseCycle = ReviewCycle;
        }

        State = RunState.UnderReview;
    }

    /// <summary>A new cycle starts with no passes: the previous cycle's are history, not state.</summary>
    private void StartCycleIfNew(int cycle)
    {
        if (cycle == ReviewCycle)
        {
            return;
        }

        ReviewCycle = cycle;
        _inFlightReviewPasses.Clear();
        _completedReviewPasses.Clear();
        _cycleHasPassMilestones = false;
    }

    private void AddInFlightPass(
        ReviewLens lens, Guid sessionId, Guid transcriptSessionId,
        int processId, DateTimeOffset processStartedAt, AgentModel model)
    {
        // One in-flight pass per lens: a redispatch (the daemon died between spawn and
        // record) supersedes its own orphan rather than being waited on twice.
        _inFlightReviewPasses.RemoveAll(pass => pass.Lens == lens);
        _inFlightReviewPasses.Add(new ReviewPassSession(
            lens, sessionId, transcriptSessionId, processId, processStartedAt, model));
    }

    private void RecordPassResult(
        ReviewLens lens, Guid? sessionId, AgentModel model, ReviewVerdict verdict,
        IReadOnlyList<ReviewFindingRecord> findings)
    {
        ReviewPassResult result = new(lens, sessionId, model, verdict, findings);
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
            || ReviewLens.MissingFrom(ActiveReviewLenses, _completedReviewPasses.Select(pass => pass.Lens)).Count > 0)
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
    /// read the tip and found nothing — so a single residual, a single settled track, or a
    /// human's own merge-ready resolution is enough to make the ending Settled instead.
    /// </summary>
    public ReviewSettlement DeriveSettlement() =>
        _humanEndedTheLoop
        || _reviewResiduals.Count > 0
        || _concludedReviewTracks.Any(track => track.Settlement == ReviewSettlement.Settled)
            ? ReviewSettlement.Settled
            : ReviewSettlement.Clean;

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
    /// The three counts are deliberately not deduplicated against each other. Only the routing
    /// pair is, because a failed routing and its retry are one export attempted twice. A defect
    /// one track fixed unreviewed and another exported really did meet both ends, and a human
    /// deciding how far to trust this pull request should be told about both.
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
            failed.Count);
    }

    /// <summary>This disposition's residuals with every repeat of a place already seen dropped.</summary>
    private List<ReviewResidual> PerDefect(ReviewResidualDisposition disposition)
    {
        List<ReviewResidual> distinct = [];
        foreach (ReviewResidual residual in _reviewResiduals.Where(residual => residual.Disposition == disposition))
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

    public void Apply(ReviewRerequested @event) => ReviewRerequestCount++;

    // No state change: a countersign request is a question asked, not a finding received,
    // so the run stays AwaitingReview while the monitor watches for the answer.
    public void Apply(ReviewRerequestedAfterFixes @event) => ReviewRerequestsAfterFixes++;

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
