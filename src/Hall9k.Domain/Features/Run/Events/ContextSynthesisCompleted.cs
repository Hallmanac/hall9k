namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// How the synthesis session ended (Decisions Log #36). A session that died, timed out, or
/// returned nothing usable records <c>Synthesized: false</c> and the launch falls back to the
/// raw handoffs — condensing is an optimization over a context that already exists, so it is
/// never allowed to block a dispatch.
/// </summary>
public sealed record ContextSynthesisCompleted(
    Guid Id,
    bool Synthesized,
    DateTimeOffset CompletedAt);
