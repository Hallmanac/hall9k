using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One persona review session that died and was carried on past (idea b9b09779, piece 1): which
/// persona owned it and what was observed, so the findings report names the gap instead of
/// presenting a report that merely looks complete.
/// </summary>
public sealed record ReviewPersonaSessionFailure(ReviewPersona Persona, string Reason, DateTimeOffset FailedAt);
