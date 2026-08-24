using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Run.Projections;

public sealed class RunDetails
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid NodeId { get; set; }
    public Guid OwnerId { get; set; }
    public int LeaseGeneration { get; set; }
    public Guid SessionId { get; set; }
    public string WorktreePath { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    /// <summary>
    /// Where this run's artifacts live, as recorded on <see cref="RunDispatched"/>. A stream
    /// written before the field existed falls back to <see cref="RunPaths.GlobalDirectory"/> —
    /// the same place its files have always actually been — so every reader can use this
    /// property directly with no fallback of its own to remember.
    /// </summary>
    public string RunDirectory { get; set; } = string.Empty;
    public string ExecutorMode { get; set; } = string.Empty;
    /// <summary>The model the build session was spawned on (Decisions Log #33); Unknown for runs dispatched before the chain existed.</summary>
    public AgentModel Model { get; set; } = AgentModel.Unknown;
    /// <summary>The model of the latest review or fix session, so a tiered configuration stays visible per leg.</summary>
    public AgentModel ReviewModel { get; set; } = AgentModel.Unknown;
    public RunState State { get; set; } = RunState.Unknown;
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    /// <summary>
    /// Every agent session the run has in flight, as the stream last recorded it. Empty when the
    /// last thing recorded was a session ending, which is the honest reading of "nothing is
    /// running" — the run state alone cannot say (a run sits in UnderReview both while a pass
    /// reads and between passes).
    /// <para>
    /// A list rather than a slot, because a review cycle dispatches one pass per active track and
    /// both read at once (Decisions Log #59): each pass carries its own lens and its own process
    /// identity, so the phase line can say which track is still out and the display can observe
    /// each process rather than assert anything about it (Decisions Log #66). PID plus start time
    /// is a process identity (the reuse guard, log #2), and without both there is nothing safe to
    /// check. A document written before this shape existed carries no sessions and therefore reads
    /// as "no session observed", which is what it honestly is; a run still alive rewrites its
    /// document on its next event, so nothing needs backfilling. Origin incident (2026-08-22): the
    /// board said the lane was quiet while a fix agent was editing the worktree, and the
    /// orchestrator nearly rewrote history under it.
    /// </para>
    /// </summary>
    public List<ActiveSession> ActiveSessions { get; set; } = [];
    public string? PullRequestUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    public DateTimeOffset? PullRequestMergedAt { get; set; }
    public List<string> FailingChecks { get; set; } = [];
    public int UnresolvedReviewThreads { get; set; }
    /// <summary>
    /// How many of <see cref="UnresolvedReviewThreads"/> a human started (Decisions Log #62).
    /// Null when the observation predates counting reviewers other than Copilot — an
    /// unrecorded breakdown, never a claimed zero.
    /// </summary>
    public int? UnresolvedHumanReviewThreads { get; set; }
    /// <summary>The last errored review observed — the monitor's dedup key: one re-request per errored review.</summary>
    public string? ErroredReviewUrl { get; set; }
    /// <summary>When a human last granted this run's task a fresh closeout budget (h9k pr resolve, Decisions Log #77, backlog 45); null until one lands.</summary>
    public DateTimeOffset? HumanGrantedAt { get; set; }
    /// <summary>Errored-review re-requests issued for this run; adds to the task's CloseoutAttempts against the shared budget.</summary>
    public int ReviewRerequestCount { get; set; }
    /// <summary>
    /// Countersign re-requests issued after this run pushed its fixes (Decisions Log #62).
    /// Its own counter, deliberately beside the closeout budget rather than inside it: the
    /// cap is summed across the task's runs, and at most one is ever issued per run.
    /// </summary>
    public int ReviewRerequestsAfterFixes { get; set; }
    public long InputTokens { get; set; }
    /// <summary>Input served from the prompt cache — priced apart from fresh input, so counted apart.</summary>
    public long CacheReadInputTokens { get; set; }
    /// <summary>Input written into the prompt cache — priced apart from fresh input, so counted apart.</summary>
    public long CacheCreationInputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>As reported by the agent result, never recomputed from the token counts.</summary>
    public decimal? CostUsd { get; set; }
    public List<string> FailedGates { get; set; } = [];
    /// <summary>
    /// The gate whose one infrastructure-classified retry is in flight, from the last
    /// <see cref="GateRetried"/> the stream carries — null once the run's verification
    /// resolves. Read back on adoption (backlog 53, Copilot review on PR #36): the retry
    /// budget lives only in <c>VerificationRunner.VerifyAsync</c>'s local state, so a daemon
    /// that dies between the retry's <see cref="GateRetried"/> commit and the gate's
    /// resolution would otherwise resume with no memory of the retry already spent, and the
    /// daemon's startup adoption sweep could grant the same gate a second one.
    /// </summary>
    public string? PendingGateRetry { get; set; }
    /// <summary>Pre-PR review loop (log #24): which round of review the run is on, from 1.</summary>
    public int ReviewCycle { get; set; }
    public ReviewVerdict LastReviewVerdict { get; set; } = ReviewVerdict.Unknown;
    /// <summary>
    /// How the review loop ended (Decisions Log #63): Clean when a reviewer read the final tip
    /// and found nothing, Settled when the severity gate, scope routing, or a human's park
    /// resolution ended it. Unknown while the loop runs, and Unknown forever for a run whose
    /// review was already in flight before tracks existed — that is an honest gap, not a
    /// clean bill of health.
    /// </summary>
    public ReviewSettlement ReviewSettlement { get; set; } = ReviewSettlement.Unknown;
    /// <summary>Residual findings the loop fixed without a reviewer ever reading the fix (log #63).</summary>
    public int ReviewResidualsFixed { get; set; }
    /// <summary>Residual findings routed to draft bug tasks instead of fixed in this pull request (log #63).</summary>
    public int ReviewResidualsRouted { get; set; }
    /// <summary>Residual findings meant for a draft bug task that could not be created — no draft exists for these (log #63).</summary>
    public int ReviewResidualsRoutingFailed { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>
    /// The reason recorded when the closeout monitor observed this run's pull request closed
    /// without a merge. Named rather than written twice, because a reader has to be able to tell
    /// that case apart from every other way a run reaches <see cref="RunState.Failed"/>: it is the
    /// one where putting the pull request back under watch achieves nothing, and the attention
    /// line composes a different lever for it.
    /// </summary>
    public const string PullRequestClosedWithoutMerge = "Pull request closed without merge.";

    /// <summary>Why closeout was handed to the human — parked is a waiting state, not a failure.</summary>
    public string? ParkedReason { get; set; }
    /// <summary>
    /// Whether this run handed anything down at true closeout, and when not, why (Decisions Log
    /// #36). Unknown on every run that has not closed out yet, and on streams written before
    /// handoffs existed — those replay as Unknown rather than as a reconstruction.
    /// </summary>
    public HandoffOutcome HandoffOutcome { get; set; } = HandoffOutcome.Unknown;
    /// <summary>The bounded handoff text a dependent's context is built from; null when the outcome records an absence.</summary>
    public string? HandoffSummary { get; set; }
    /// <summary>Synthesis sessions dispatched to condense this run's own blockers' handoffs (log #36).</summary>
    public int ContextSynthesisSessions { get; set; }
    /// <summary>
    /// Whether the last synthesis pass produced a usable document. Read alongside
    /// <see cref="ContextSynthesisSessions"/>: zero sessions means the fan-in never warranted
    /// one, while a session with this false means the pass could not deliver and the run
    /// started on the raw handoffs instead.
    /// </summary>
    public bool ContextSynthesized { get; set; }
    public DateTimeOffset DispatchedAt { get; set; }
    public bool IsFollowUp { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class RunDetailsProjection : SingleStreamProjection<RunDetails, Guid>
{
    public RunDetails Create(IEvent<RunDispatched> @event) => new()
    {
        Id = @event.Data.Id,
        TaskId = @event.Data.TaskId,
        NodeId = @event.Data.NodeId,
        OwnerId = @event.Data.OwnerId,
        LeaseGeneration = @event.Data.LeaseGeneration,
        SessionId = @event.Data.SessionId,
        WorktreePath = @event.Data.WorktreePath,
        Branch = @event.Data.Branch,
        RunDirectory = @event.Data.RunDirectory.IsNotBlank()
            ? @event.Data.RunDirectory
            : RunPaths.GlobalDirectory(@event.Data.Id),
        ExecutorMode = @event.Data.ExecutorMode,
        Model = @event.Data.Model ?? AgentModel.Unknown,
        State = RunState.Dispatched,
        DispatchedAt = @event.Data.DispatchedAt,
        IsFollowUp = @event.Data.IsFollowUp,
    };

    public void Apply(IEvent<RunProcessStarted> @event, RunDetails view)
    {
        view.ProcessId = @event.Data.ProcessId;
        view.ProcessStartedAt = @event.Data.ProcessStartedAt;
        StartSession(view, AgentRole.Build, ReviewLens.Unknown, @event.Data.ProcessId, @event.Data.ProcessStartedAt);
        view.State = RunState.Running;
    }

    // A resume records the new process whole — pid plus start time (log #2's identity) — since
    // the budget retry became a real resume path (backlog 40). It used to carry only a pid
    // (log #5's exit-and-resume), which left the session half-identified and its liveness
    // unobservable; the spawn already knows when it started, so recording it is the honest
    // reading rather than a guess.
    public void Apply(IEvent<RunResumed> @event, RunDetails view)
    {
        view.ProcessId = @event.Data.ProcessId;
        view.ProcessStartedAt = @event.Data.ProcessStartedAt;
        StartSession(
            view, AgentRole.Build, ReviewLens.Unknown, @event.Data.ProcessId, @event.Data.ProcessStartedAt);
        // A budget park is the only park RunResumed currently clears (backlog 40); a review
        // park's ParkedReason is only ever cleared by ReviewParkResolved, a human's own act.
        view.ParkedReason = null;
        view.State = RunState.Running;
    }

    public void Apply(IEvent<AgentSessionCompleted> @event, RunDetails view)
    {
        EndSessions(view);
        view.State = RunState.Verifying;
    }

    public void Apply(IEvent<TokensRecorded> @event, RunDetails view)
    {
        view.InputTokens += @event.Data.InputTokens;
        view.CacheReadInputTokens += @event.Data.CacheReadInputTokens;
        view.CacheCreationInputTokens += @event.Data.CacheCreationInputTokens;
        view.OutputTokens += @event.Data.OutputTokens;
        if (@event.Data.CostUsd is not null)
        {
            view.CostUsd = (view.CostUsd ?? 0m) + @event.Data.CostUsd.Value;
        }
    }

    public void Apply(IEvent<VerificationFailed> @event, RunDetails view)
    {
        view.FailedGates = [.. @event.Data.FailedGates];
        view.PendingGateRetry = null;
    }

    public void Apply(IEvent<VerificationPassed> @event, RunDetails view)
    {
        view.FailedGates = [];
        view.PendingGateRetry = null;
    }

    // The gate's own retry going in, not the run's terminal outcome: cleared only by
    // VerificationFailed/VerificationPassed above, since a gate that resolves this event's
    // retry still leaves later gates in the same VerifyAsync call to run.
    public void Apply(IEvent<GateRetried> @event, RunDetails view) => view.PendingGateRetry = @event.Data.Gate;

    public void Apply(IEvent<ReviewDispatched> @event, RunDetails view)
    {
        if (@event.Data.Cycle != view.ReviewCycle)
        {
            // A new cycle starts with nothing in flight from the last one; a second dispatch
            // inside the same cycle is the other lens joining it (log #59), and joining is
            // exactly what it does — the first lens's pass keeps its own record.
            EndSessions(view);
        }

        view.ReviewCycle = @event.Data.Cycle;
        view.ReviewModel = @event.Data.Model ?? AgentModel.Unknown;
        StartSession(
            view, AgentRole.Review, @event.Data.Lens, @event.Data.ProcessId, @event.Data.ProcessStartedAt);
        // Mirrors RunResumed and ReviewFixDispatched: a redispatched review pass over a
        // budget park (backlog 40) is live work again, so the stale park reason must go too.
        view.ParkedReason = null;
        view.State = RunState.UnderReview;
    }

    public void Apply(IEvent<ReviewFixDispatched> @event, RunDetails view)
    {
        view.ReviewModel = @event.Data.Model ?? AgentModel.Unknown;
        StartSession(view, AgentRole.Fix, ReviewLens.Unknown, @event.Data.ProcessId, @event.Data.ProcessStartedAt);
        // Mirrors RunAggregate: a fix session redispatched over a budget park (backlog 40)
        // is the one path that needs this stated, since nothing else moves State off BudgetParked.
        view.ParkedReason = null;
        view.State = RunState.UnderReview;
    }

    /// <summary>
    /// The fix session ended, whatever it decided. The gates run next (or a park does), and
    /// neither is a session, so nothing is left for the phase line to claim is running.
    /// </summary>
    public void Apply(IEvent<ReviewFixCompleted> @event, RunDetails view) => EndSessions(view);

    // A resumed session keeps the model it started with, so this records rather than replaces.
    public void Apply(IEvent<ReviewVerdictReprompted> @event, RunDetails view)
    {
        view.ReviewModel = @event.Data.Model ?? AgentModel.Unknown;
        StartSession(
            view, AgentRole.Review, @event.Data.Lens, @event.Data.ProcessId, @event.Data.ProcessStartedAt);
    }

    /// <summary>
    /// One lens finished reading. Only that pass's session is cleared: the pass left in flight is
    /// the fact the phase line needs — a cycle with the adversarial track still out is not a cycle
    /// nobody is working on — and its process is what the display observes for liveness. Clearing
    /// the sibling here (or holding a single slot for both) is how a healthy cycle came to read as
    /// a dead one, since the passes finish in whichever order their diffs allow.
    /// </summary>
    public void Apply(IEvent<ReviewPassCompleted> @event, RunDetails view)
    {
        ReviewLens lens = @event.Data.Lens ?? ReviewLens.Unknown;
        view.ActiveSessions.RemoveAll(session => session.Role == AgentRole.Review && session.Lens == lens);
    }

    public void Apply(IEvent<ReviewCompleted> @event, RunDetails view)
    {
        view.LastReviewVerdict = @event.Data.Verdict;
        // A run whose passes predate per-lens milestones records no ReviewPassCompleted at all,
        // so the cycle's own conclusion is where its sessions are known to have ended.
        EndSessions(view);
    }

    /// <summary>
    /// The terminal verdict is MergeReady however the loop got here; the settlement beside it
    /// is what keeps a settled ending from reading like a clean one (log #63).
    /// <para>
    /// The residual counts are read off this event rather than tallied from the
    /// <see cref="ReviewTrackConcluded"/> and <see cref="ReviewFindingRouted"/> events the
    /// stream carries, so the number this view reports and the number the terminal event
    /// states are the same number rather than two derivations that agree until they do not — an unrecognized disposition, say, that a
    /// tally here would have to guess a column for.
    /// </para>
    /// </summary>
    public void Apply(IEvent<ReviewSettled> @event, RunDetails view)
    {
        EndSessions(view);
        view.LastReviewVerdict = ReviewVerdict.MergeReady;
        view.ReviewSettlement = @event.Data.Settlement;
        view.ReviewResidualsFixed = @event.Data.ResidualsFixed;
        view.ReviewResidualsRouted = @event.Data.ResidualsRouted;
        view.ReviewResidualsRoutingFailed = @event.Data.ResidualsRoutingFailed;
    }

    public void Apply(IEvent<ReviewParked> @event, RunDetails view)
    {
        view.ParkedReason = @event.Data.Reason;
        EndSessions(view);
        view.State = RunState.ReviewParked;
    }

    public void Apply(IEvent<ReviewParkResolved> @event, RunDetails view)
    {
        view.LastReviewVerdict = @event.Data.Verdict;
        view.ParkedReason = null;
        // The resume sweep re-dispatches; until it does, nothing is running.
        EndSessions(view);
        view.State = RunState.UnderReview;
    }

    public void Apply(IEvent<PullRequestOpened> @event, RunDetails view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.PullRequestNumber = @event.Data.PullRequestNumber;
        EndSessions(view);
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestUpdated> @event, RunDetails view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.PullRequestNumber = @event.Data.PullRequestNumber;
        EndSessions(view);
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestChecksFailed> @event, RunDetails view)
    {
        view.FailingChecks = [.. @event.Data.FailedChecks];
        view.State = RunState.ChecksFailing;
    }

    public void Apply(IEvent<ReviewFeedbackReceived> @event, RunDetails view)
    {
        view.UnresolvedReviewThreads = @event.Data.UnresolvedThreadCount;
        view.UnresolvedHumanReviewThreads = @event.Data.UnresolvedHumanThreadCount;
        view.State = RunState.ReviewPending;
    }

    public void Apply(IEvent<ReviewErrored> @event, RunDetails view)
    {
        view.ErroredReviewUrl = @event.Data.ReviewUrl;
        view.State = RunState.ReviewPending;
    }

    public void Apply(IEvent<ReviewRerequested> @event, RunDetails view) => view.ReviewRerequestCount++;

    // The run stays AwaitingReview: a countersign request is a question asked, not a
    // finding received, and the monitor must keep watching this pull request for the answer.
    public void Apply(IEvent<ReviewRerequestedAfterFixes> @event, RunDetails view) =>
        view.ReviewRerequestsAfterFixes++;

    public void Apply(IEvent<CloseoutParked> @event, RunDetails view)
    {
        view.ParkedReason = @event.Data.Reason;
        EndSessions(view);
        view.State = RunState.CloseoutParked;
    }

    public void Apply(IEvent<CloseoutBudgetGranted> @event, RunDetails view) =>
        view.HumanGrantedAt = @event.Data.GrantedAt;

    public void Apply(IEvent<PullRequestMerged> @event, RunDetails view) =>
        view.PullRequestMergedAt = @event.Data.MergedAt;

    public void Apply(IEvent<PullRequestClosed> @event, RunDetails view)
    {
        view.FailureReason = RunDetails.PullRequestClosedWithoutMerge;
        EndSessions(view);
        view.State = RunState.Failed;
        view.FinishedAt = @event.Data.ObservedAt;
    }

    public void Apply(IEvent<RunHandoffRecorded> @event, RunDetails view)
    {
        view.HandoffOutcome = @event.Data.Outcome ?? HandoffOutcome.Unknown;
        view.HandoffSummary = @event.Data.Summary;
    }

    public void Apply(IEvent<ContextSynthesisDispatched> @event, RunDetails view)
    {
        view.ContextSynthesisSessions++;
        StartSession(
            view, AgentRole.Synthesis, ReviewLens.Unknown, @event.Data.ProcessId, @event.Data.ProcessStartedAt);
    }

    public void Apply(IEvent<ContextSynthesisCompleted> @event, RunDetails view)
    {
        view.ContextSynthesized = @event.Data.Synthesized;
        EndSessions(view);
    }

    public void Apply(IEvent<RunCompleted> @event, RunDetails view)
    {
        EndSessions(view);
        view.State = RunState.Completed;
        view.FinishedAt = @event.Data.CompletedAt;
    }

    public void Apply(IEvent<RunBudgetExhausted> @event, RunDetails view)
    {
        view.ParkedReason =
            "token budget exhausted - resumes when the subscription window resets";
        view.State = RunState.BudgetParked;
    }

    public void Apply(IEvent<RunFailed> @event, RunDetails view)
    {
        EndSessions(view);
        view.State = RunState.Failed;
        view.FailureReason = @event.Data.Reason;
        view.FinishedAt = @event.Data.FailedAt;
    }

    public void Apply(IEvent<RunKilled> @event, RunDetails view)
    {
        EndSessions(view);
        view.State = RunState.Killed;
        view.FailureReason = @event.Data.Reason;
        view.FinishedAt = @event.Data.KilledAt;
    }

    public void Apply(IEvent<RunSuperseded> @event, RunDetails view)
    {
        EndSessions(view);
        view.State = RunState.Superseded;
        view.FinishedAt = @event.Data.SupersededAt;
    }

    /// <summary>
    /// Records the session the run just spawned, so a reader can ask the operating system
    /// whether it is still there instead of inferring it from the run state.
    /// <para>
    /// A review pass joins whatever else the cycle already has out and replaces only its own
    /// track's record, which is what a verdict re-prompt is: the same lens, resumed under a new
    /// process. Every other role runs alone, so it replaces the lot — a build, fix or synthesis
    /// session starting is proof that whatever came before it is over.
    /// </para>
    /// </summary>
    private static void StartSession(
        RunDetails view, AgentRole role, ReviewLens? lens, int processId, DateTimeOffset? startedAt)
    {
        ReviewLens track = lens ?? ReviewLens.Unknown;
        if (role == AgentRole.Review)
        {
            view.ActiveSessions.RemoveAll(session => session.Role == AgentRole.Review && session.Lens == track);
        }
        else
        {
            view.ActiveSessions.Clear();
        }

        view.ActiveSessions.Add(new ActiveSession(role, track, processId, startedAt));
    }

    /// <summary>
    /// Every session ended: the run may well still be busy (gates run in the daemon's own
    /// process), but no agent process is recorded as running, and saying so is the point.
    /// </summary>
    private static void EndSessions(RunDetails view) => view.ActiveSessions.Clear();
}
