namespace Hall9k.Domain.Features.Tasks.Documents;

/// <summary>
/// Mutable lease telemetry, NOT an event (Decisions Log #7): claims are TaskClaimed events;
/// heartbeat renewals just overwrite HeartbeatAt here. Id == TaskId.
/// </summary>
public sealed class TaskLease
{
    public Guid Id { get; set; }
    public Guid NodeId { get; set; }
    public int LeaseGeneration { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
}
