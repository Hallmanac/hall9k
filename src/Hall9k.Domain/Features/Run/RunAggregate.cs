using Hall9k.Domain.Features.Run.Events;
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

    /// <summary>Review re-requests issued for this run; adds to the task's CloseoutAttempts against the shared budget.</summary>
    public int ReviewRerequestCount { get; private set; }

    /// <summary>The pre-PR review loop (log #24): which round of review the run is on, from 1.</summary>
    public int ReviewCycle { get; private set; }
    /// <summary>Automatic fix sessions dispatched so far — checked against DaemonOptions.MaxAutomaticReviewFixRuns.</summary>
    public int ReviewFixRuns { get; private set; }
    /// <summary>The current cycle's merged verdict across its lenses (log #59), not any single pass's.</summary>
    public ReviewVerdict LastReviewVerdict { get; private set; } = ReviewVerdict.Unknown;
    public ReviewPhase ReviewPhase { get; private set; } = ReviewPhase.None;

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
        RecordPassResult(lens, pass?.TranscriptSessionId, pass?.Model ?? AgentModel.Unknown, @event.Verdict);
        _inFlightReviewPasses.RemoveAll(inFlight => inFlight.Lens == lens);
        ReviewPhase = DeriveReviewPhase();
    }

    public void Apply(ReviewCompleted @event)
    {
        // A pass still in flight when the cycle concludes belongs to a stream written before
        // lenses existed, where one ReviewCompleted WAS the whole cycle; it is retired here
        // with the cycle's verdict, which for that stream is the verdict it actually returned.
        foreach (ReviewPassSession pass in _inFlightReviewPasses)
        {
            RecordPassResult(pass.Lens, pass.TranscriptSessionId, pass.Model, @event.Verdict);
        }

        _inFlightReviewPasses.Clear();
        LastReviewVerdict = @event.Verdict;
        ReviewPhase = PhaseFor(@event.Verdict);
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
        PendingHumanFindings = null;
        ActiveFixSessionId = @event.SessionId;
        ActiveFixProcessId = @event.ProcessId;
        ActiveFixProcessStartedAt = @event.ProcessStartedAt;
        ActiveFixSessionModel = @event.Model ?? AgentModel.Unknown;
        ReviewPhase = ReviewPhase.AwaitingFix;
    }

    public void Apply(ReviewFixCompleted @event)
    {
        ClearActiveFixSession();
        ReviewPhase = @event.Outcome == ReviewFixOutcome.Disputed ? ReviewPhase.Disputed : ReviewPhase.Reverify;
    }

    public void Apply(ReviewParked @event)
    {
        ReviewPhase = ReviewPhase.Parked;
        State = RunState.ReviewParked;
    }

    public void Apply(ReviewParkResolved @event)
    {
        if (@event.Verdict == ReviewVerdict.MergeReady)
        {
            LastReviewVerdict = ReviewVerdict.MergeReady;
            ReviewPhase = ReviewPhase.MergeReady;
        }
        else
        {
            LastReviewVerdict = ReviewVerdict.NeedsFixes;
            ReviewPhase = ReviewPhase.FixNeeded;
            PendingHumanFindings = @event.Reason;
            // Like a manual pr resolve, the human asking is a fresh grant (log #22):
            // the spent automatic fix budget must not instantly re-park the run.
            ReviewFixRuns = 0;
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

    private void RecordPassResult(ReviewLens lens, Guid? sessionId, AgentModel model, ReviewVerdict verdict)
    {
        ReviewPassResult result = new(lens, sessionId, model, verdict);
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
    /// Where the cycle stands once a pass lands: still waiting while any lens is in flight or
    /// any lens has yet to look at all, otherwise the merged verdict of every lens (log #59).
    /// A cycle one lens short is not a cycle, whatever the lenses that did answer said.
    /// </summary>
    private ReviewPhase DeriveReviewPhase() =>
        _inFlightReviewPasses.Count > 0
        || ReviewLens.MissingFrom(_completedReviewPasses.Select(pass => pass.Lens)).Count > 0
            ? ReviewPhase.AwaitingVerdict
            : PhaseFor(ReviewVerdict.Merge(_completedReviewPasses.Select(pass => pass.Verdict)));

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

    public void Apply(CloseoutParked @event) => State = RunState.CloseoutParked;

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

    public void Apply(RunFailed @event) => State = RunState.Failed;

    public void Apply(RunKilled @event) => State = RunState.Killed;

    public void Apply(RunSuperseded @event) => State = RunState.Superseded;
}
