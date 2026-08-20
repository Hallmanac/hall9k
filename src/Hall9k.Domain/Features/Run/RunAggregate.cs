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
    public ReviewVerdict LastReviewVerdict { get; private set; } = ReviewVerdict.Unknown;
    public ReviewPhase ReviewPhase { get; private set; } = ReviewPhase.None;
    /// <summary>The in-flight review or fix session, cleared when its result is recorded. Identity for adoption.</summary>
    public Guid? ActiveReviewSessionId { get; private set; }
    public int? ActiveReviewProcessId { get; private set; }
    public DateTimeOffset? ActiveReviewProcessStartedAt { get; private set; }
    public bool ActiveReviewSessionIsFix { get; private set; }
    /// <summary>The model the in-flight review or fix session was spawned on.</summary>
    public AgentModel ActiveReviewSessionModel { get; private set; } = AgentModel.Unknown;
    /// <summary>The last completed review session — the resume target for the one verdict re-prompt.</summary>
    public Guid? LastReviewSessionId { get; private set; }
    /// <summary>The model that session runs on; a resume keeps it, so the re-prompt records it rather than re-resolving (log #33).</summary>
    public AgentModel LastReviewSessionModel { get; private set; } = AgentModel.Unknown;
    /// <summary>The highest cycle whose verdict re-prompt was already spent (0 = never). One re-prompt per cycle, then park.</summary>
    public int VerdictRepromptedCycle { get; private set; }
    /// <summary>Human findings from a needs-fixes park resolution, consumed by the next fix dispatch.</summary>
    public string? PendingHumanFindings { get; private set; }

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
        ReviewCycle = @event.Cycle;
        ActiveReviewSessionId = @event.SessionId;
        ActiveReviewProcessId = @event.ProcessId;
        ActiveReviewProcessStartedAt = @event.ProcessStartedAt;
        ActiveReviewSessionIsFix = false;
        ActiveReviewSessionModel = @event.Model ?? AgentModel.Unknown;
        ReviewPhase = ReviewPhase.AwaitingVerdict;
        State = RunState.UnderReview;
    }

    public void Apply(ReviewCompleted @event)
    {
        LastReviewVerdict = @event.Verdict;
        if (ActiveReviewSessionId is not null)
        {
            LastReviewSessionId = ActiveReviewSessionId;
            LastReviewSessionModel = ActiveReviewSessionModel;
        }

        ClearActiveReviewSession();
        ReviewPhase = @event.Verdict == ReviewVerdict.MergeReady
            ? ReviewPhase.MergeReady
            : @event.Verdict == ReviewVerdict.NeedsFixes
                ? ReviewPhase.FixNeeded
                : ReviewPhase.VerdictMissing;
    }

    public void Apply(ReviewVerdictReprompted @event)
    {
        ActiveReviewSessionId = @event.SessionId;
        ActiveReviewProcessId = @event.ProcessId;
        ActiveReviewProcessStartedAt = @event.ProcessStartedAt;
        ActiveReviewSessionIsFix = false;
        ActiveReviewSessionModel = @event.Model ?? AgentModel.Unknown;
        // The resumed transcript continues the ORIGINAL session; SessionId above is only
        // this leg's artifact identity.
        LastReviewSessionId = @event.ResumedSessionId;
        VerdictRepromptedCycle = @event.Cycle;
        ReviewPhase = ReviewPhase.AwaitingVerdict;
    }

    public void Apply(ReviewFixDispatched @event)
    {
        ReviewFixRuns++;
        PendingHumanFindings = null;
        ActiveReviewSessionId = @event.SessionId;
        ActiveReviewProcessId = @event.ProcessId;
        ActiveReviewProcessStartedAt = @event.ProcessStartedAt;
        ActiveReviewSessionIsFix = true;
        ActiveReviewSessionModel = @event.Model ?? AgentModel.Unknown;
        ReviewPhase = ReviewPhase.AwaitingFix;
    }

    public void Apply(ReviewFixCompleted @event)
    {
        ClearActiveReviewSession();
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

    private void ClearActiveReviewSession()
    {
        ActiveReviewSessionId = null;
        ActiveReviewProcessId = null;
        ActiveReviewProcessStartedAt = null;
        ActiveReviewSessionIsFix = false;
        ActiveReviewSessionModel = AgentModel.Unknown;
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

    public void Apply(RunCompleted @event) => State = RunState.Completed;

    public void Apply(RunFailed @event) => State = RunState.Failed;

    public void Apply(RunKilled @event) => State = RunState.Killed;

    public void Apply(RunSuperseded @event) => State = RunState.Superseded;
}
