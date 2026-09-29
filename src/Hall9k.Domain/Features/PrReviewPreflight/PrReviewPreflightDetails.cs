using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// The read view of one pull-request review pre-flight (idea 6be68ee2, finding 1, phase one):
/// what <c>RunLauncher</c> reads to find the verdict recorded for a task's current head oid, and
/// what the adoption sweep reads to tell a still-running pre-flight apart from one whose process
/// died before it could complete. <see cref="CompletedAt"/> null means still running — UNLESS
/// <see cref="AbandonedAt"/> is set, in which case it never will (a permanently-incomplete row:
/// see <see cref="PrReviewPreflightAbandoned"/>'s own doc).
/// </summary>
public sealed class PrReviewPreflightDetails
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>Mirrors <see cref="PrReviewPreflightDispatched.DispatchingRunId"/>.</summary>
    public Guid DispatchingRunId { get; set; }
    public Guid NodeId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string HeadRefOid { get; set; } = string.Empty;
    public IReadOnlyList<string> Surfaces { get; set; } = [];
    public DateTimeOffset DispatchedAt { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Safe { get; set; }
    public string Verdict { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset? AbandonedAt { get; set; }
    /// <summary>Mirrors <see cref="PrReviewPreflightDispatched.IsMentionFollowUp"/>.</summary>
    public bool IsMentionFollowUp { get; set; }
}

public sealed partial class PrReviewPreflightDetailsProjection : SingleStreamProjection<PrReviewPreflightDetails, Guid>
{
    public PrReviewPreflightDetails Create(IEvent<PrReviewPreflightDispatched> @event) => new()
    {
        Id = @event.Data.Id,
        TaskId = @event.Data.TaskId,
        DispatchingRunId = @event.Data.DispatchingRunId,
        NodeId = @event.Data.NodeId,
        Model = @event.Data.Model.Value,
        HeadRefOid = @event.Data.HeadRefOid,
        Surfaces = @event.Data.Surfaces,
        DispatchedAt = @event.Data.DispatchedAt,
        IsMentionFollowUp = @event.Data.IsMentionFollowUp,
    };

    public void Apply(IEvent<PrReviewPreflightProcessStarted> @event, PrReviewPreflightDetails view)
    {
        view.ProcessId = @event.Data.ProcessId;
        view.ProcessStartedAt = @event.Data.ProcessStartedAt;
    }

    public void Apply(IEvent<PrReviewPreflightCompleted> @event, PrReviewPreflightDetails view)
    {
        view.CompletedAt = @event.Data.CompletedAt;
        view.Safe = @event.Data.Safe;
        view.Verdict = @event.Data.Verdict;
        view.Reason = @event.Data.Reason;
    }

    public void Apply(IEvent<PrReviewPreflightAbandoned> @event, PrReviewPreflightDetails view)
    {
        view.AbandonedAt = @event.Data.AbandonedAt;
    }

    public void Apply(IEvent<PrReviewPreflightReclaimed> @event, PrReviewPreflightDetails view)
    {
        view.DispatchingRunId = @event.Data.DispatchingRunId;
    }
}
