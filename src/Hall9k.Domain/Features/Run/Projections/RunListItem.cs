using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Run.Projections;

/// <summary>Lean row for h9k status and the by-TaskId join (no multi-stream projection).</summary>
public sealed class RunListItem
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid NodeId { get; set; }
    public int LeaseGeneration { get; set; }
    public RunState State { get; set; } = RunState.Unknown;
    /// <summary>The model the build session was spawned on, shown by h9k task show (Decisions Log #33).</summary>
    public AgentModel Model { get; set; } = AgentModel.Unknown;
    /// <summary>
    /// The pre-PR review stage composition this run actually ran under, resolved once at dispatch
    /// and frozen for the run's whole lifetime (task: the review pipeline's stage composition
    /// becomes configuration recorded per run), shown by h9k task show so no future investigation
    /// ever has to guess which pipeline shape a session ran (the stranding investigation's
    /// three-round lesson). FullPipeline on every stream written before this field existed.
    /// </summary>
    public ReviewStageComposition ReviewStageComposition { get; set; } = ReviewStageComposition.FullPipeline;
    public string? PullRequestUrl { get; set; }
    public DateTimeOffset DispatchedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>
    /// Where this run's artifacts lived at dispatch, as recorded on <see cref="RunDispatched"/>.
    /// A task's directory can move across the <c>tasks</c>/<c>tasks/_archive</c> boundary and
    /// back after dispatch (backlog 51, PLAN.md §16 #84), so this is a dispatch-time record, not
    /// a live pointer — resolve it through <see cref="RunPaths.ResolveCurrentDirectory"/> before
    /// use rather than trusting it verbatim. A stream written before the field existed falls back
    /// to <see cref="RunPaths.GlobalDirectory"/> — the same place its files have always actually
    /// been.
    /// </summary>
    public string RunDirectory { get; set; } = string.Empty;
    /// <summary>
    /// Each gate's own wall-clock duration from this run's most recently recorded verification
    /// pass or failure (task: gate wall-clock duration is recorded and surfaced), replaced whole
    /// on each new <see cref="VerificationPassed"/>/<see cref="VerificationFailed"/> rather than
    /// accumulated — the same "last recorded" shape <c>RunDetails.FailedGates</c> already takes.
    /// Null on a stream written before this field existed, or on one that has not verified yet —
    /// an unobserved duration, never a claimed zero.
    /// </summary>
    public List<GateDuration>? GateDurations { get; set; }
    /// <summary>
    /// True only for a run that CloseoutEngine's missing-run sweep reconstructed rather than one
    /// that actually dispatched — it never ran a build session, so it never wrote a transcript
    /// (independent pre-PR review, cycle 1, conformance: <c>h9k logs</c>'s own newest-run pick must
    /// skip a run this flag marks in favor of one that actually produced a transcript, rather than
    /// resolving a directory nothing was ever written to). This projection is Inline with no
    /// backfill, so a run reconstructed before this field shipped keeps this flag reading false
    /// forever: <see cref="RunRecordReconstructed"/>'s own creation commits every event that
    /// stream will ever receive (<c>PullRequestMerged</c>/<c>RunHandoffRecorded</c>/
    /// <c>RunCompleted</c>, same commit), so nothing ever rewrites the document to pick the new key
    /// up (independent pre-PR review, cycle 1, both lenses). <see cref="LooksReconstructed"/> is
    /// the read-site fallback that catches those already-written stubs too.
    /// </summary>
    public bool IsReconstructed { get; set; }

    /// <summary>
    /// What <c>h9k logs</c> actually keys its stub-skip on, rather than <see cref="IsReconstructed"/>
    /// alone: that flag is permanently absent on every reconstruction already written before it
    /// shipped (this field's own doc above), so a document-shape fallback is needed with no
    /// migration to carry it. <see cref="LeaseGeneration"/> serves: <see cref="RunRecordReconstructed"/>
    /// deliberately records nothing a real dispatch would have observed (that event's own doc),
    /// so it is never set and reads the type default, 0, while a real dispatch's own
    /// <c>RunDispatched.LeaseGeneration</c> is always <c>TaskClaimed</c>'s lease generation — never
    /// less than 1, since a task's own <c>LeaseGeneration</c> starts at 0 and every claim records
    /// <c>+ 1</c> (<c>TaskDecider</c>) — and that field has been a required, non-nullable
    /// <c>RunDispatched</c> parameter since the event's own creation, so no legacy dispatched
    /// stream can read 0 either. A reconstructed run is therefore <c>LeaseGeneration == 0</c> on
    /// every document old or new, flag or no flag.
    /// </summary>
    public bool LooksReconstructed => IsReconstructed || LeaseGeneration == 0;
}

public sealed class RunListItemProjection : SingleStreamProjection<RunListItem, Guid>
{
    public RunListItem Create(IEvent<RunDispatched> @event) => new()
    {
        Id = @event.Data.Id,
        TaskId = @event.Data.TaskId,
        NodeId = @event.Data.NodeId,
        LeaseGeneration = @event.Data.LeaseGeneration,
        Model = @event.Data.Model ?? AgentModel.Unknown,
        ReviewStageComposition = @event.Data.ReviewStageComposition ?? ReviewStageComposition.FullPipeline,
        State = RunState.Dispatched,
        DispatchedAt = @event.Data.DispatchedAt,
        RunDirectory = @event.Data.RunDirectory.IsNotBlank()
            ? @event.Data.RunDirectory
            : RunPaths.GlobalDirectory(@event.Data.Id),
    };

    /// <summary>See RunDetailsProjection's own creator: a reconstructed stream for a run that never actually dispatched.</summary>
    public RunListItem Create(IEvent<RunRecordReconstructed> @event) => new()
    {
        Id = @event.Data.Id,
        TaskId = @event.Data.TaskId,
        NodeId = @event.Data.NodeId,
        State = RunState.Dispatched,
        DispatchedAt = @event.Data.ReconstructedAt,
        PullRequestUrl = @event.Data.PullRequestUrl,
        RunDirectory = RunPaths.GlobalDirectory(@event.Data.Id),
        IsReconstructed = true,
    };

    public void Apply(IEvent<RunProcessStarted> @event, RunListItem view) => view.State = RunState.Running;

    public void Apply(IEvent<RunResumed> @event, RunListItem view) => view.State = RunState.Running;

    // Mirrors RunDetailsProjection: without this, an interactive claim's run list row stays
    // at Dispatched (RunDispatched's own state) for the whole time an operator holds it, while
    // RunDetails — and the phase line built from it — already reads Running (conformance
    // review, cycle 1).
    public void Apply(IEvent<InteractiveSessionStarted> @event, RunListItem view) => view.State = RunState.Running;

    public void Apply(IEvent<AgentSessionCompleted> @event, RunListItem view)
    {
        view.State = RunState.Verifying;
        if (@event.Data.DeliveredByNodeId is { } deliveredByNodeId)
        {
            view.NodeId = deliveredByNodeId;
        }
    }

    public void Apply(IEvent<VerificationFailed> @event, RunListItem view)
    {
        if (@event.Data.GateDurations is { } durations)
        {
            view.GateDurations = [.. durations];
        }
    }

    public void Apply(IEvent<VerificationPassed> @event, RunListItem view)
    {
        if (@event.Data.GateDurations is { } durations)
        {
            view.GateDurations = [.. durations];
        }
    }

    public void Apply(IEvent<ReviewDispatched> @event, RunListItem view) => view.State = RunState.UnderReview;

    public void Apply(IEvent<ReviewParked> @event, RunListItem view) => view.State = RunState.ReviewParked;

    public void Apply(IEvent<ReviewParkResolved> @event, RunListItem view) => view.State = RunState.UnderReview;

    // Mirrors RunAggregate/RunDetails: a fix session redispatched over a budget park
    // (backlog 40) is the one path that needs this stated, since nothing else moves State
    // off BudgetParked between a review cycle's ReviewDispatched entries.
    public void Apply(IEvent<ReviewFixDispatched> @event, RunListItem view) => view.State = RunState.UnderReview;

    public void Apply(IEvent<RunBudgetExhausted> @event, RunListItem view) => view.State = RunState.BudgetParked;

    // Mirrors RunDetails/RunAggregate: a pr-review run's own conformance lens (PrReviewEngine)
    // is dispatched the same way ReviewDispatched moves a task's own review loop to
    // UnderReview, and PrReviewDelivered is that task type's h9k review resolve --merge-ready
    // equivalent of ReviewParkResolved. Without these this projection stays stuck at Verifying
    // for the whole conformance-lens window — a state meaning "the project's gates are
    // running", which a pr-review run's gates never do.
    public void Apply(IEvent<PrReviewConformanceDispatched> @event, RunListItem view) => view.State = RunState.UnderReview;

    public void Apply(IEvent<PrReviewDelivered> @event, RunListItem view) => view.State = RunState.UnderReview;

    public void Apply(IEvent<PullRequestOpened> @event, RunListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestUpdated> @event, RunListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.State = RunState.AwaitingReview;
    }

    /// <summary>
    /// State-free by design (see the event's own doc): a Failed run stays Failed. Mirrors
    /// RunDetailsProjection/RunAggregate — without this, h9k task show's "Runs" table keeps
    /// showing "-" in the PR column for a run h9k task resolve --pr just recorded one on.
    /// </summary>
    public void Apply(IEvent<PullRequestRecordedOnFailedRun> @event, RunListItem view) =>
        view.PullRequestUrl = @event.Data.PullRequestUrl;

    public void Apply(IEvent<PullRequestChecksFailed> @event, RunListItem view) => view.State = RunState.ChecksFailing;

    public void Apply(IEvent<ReviewFeedbackReceived> @event, RunListItem view) => view.State = RunState.ReviewPending;

    public void Apply(IEvent<ReviewErrored> @event, RunListItem view) => view.State = RunState.ReviewPending;

    public void Apply(IEvent<PullRequestConflictObserved> @event, RunListItem view) => view.State = RunState.Conflicting;

    public void Apply(IEvent<CloseoutParked> @event, RunListItem view) => view.State = RunState.CloseoutParked;

    public void Apply(IEvent<PullRequestClosed> @event, RunListItem view)
    {
        view.State = RunState.Failed;
        view.FinishedAt = @event.Data.ObservedAt;
    }

    public void Apply(IEvent<RunCompleted> @event, RunListItem view)
    {
        view.State = RunState.Completed;
        view.FinishedAt = @event.Data.CompletedAt;
    }

    public void Apply(IEvent<RunFailed> @event, RunListItem view)
    {
        view.State = RunState.Failed;
        view.FinishedAt = @event.Data.FailedAt;
    }

    public void Apply(IEvent<RunKilled> @event, RunListItem view)
    {
        view.State = RunState.Killed;
        view.FinishedAt = @event.Data.KilledAt;
    }

    public void Apply(IEvent<RunSuperseded> @event, RunListItem view)
    {
        view.State = RunState.Superseded;
        view.FinishedAt = @event.Data.SupersededAt;
    }
}
