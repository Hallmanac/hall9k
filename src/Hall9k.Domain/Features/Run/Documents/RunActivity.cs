namespace Hall9k.Domain.Features.Run.Documents;

/// <summary>
/// Mutable telemetry, NOT an event stream and NOT a projection (Decisions Log #11):
/// the daemon upserts this while tailing stream.jsonl. Id == RunId.
/// </summary>
public sealed class RunActivity
{
    public Guid Id { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public long StreamBytesRead { get; set; }
}
