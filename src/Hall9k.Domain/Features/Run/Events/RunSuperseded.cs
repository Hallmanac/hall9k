namespace Hall9k.Domain.Features.Run.Events;

/// <summary>A newer claim generation took over; this run's further output is discarded (log #7).</summary>
public sealed record RunSuperseded(
    Guid Id,
    int SupersededByGeneration,
    DateTimeOffset SupersededAt);
