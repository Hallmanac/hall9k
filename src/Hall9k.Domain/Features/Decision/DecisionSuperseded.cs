namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// A decision stopped binding, and why (idea d805fd8b, piece 1). Appended to the superseded
/// decision's own stream, never deleting anything: the statement, its provenance, and its whole
/// history stay queryable, and only its standing changes.
/// <para>
/// <see cref="SupersededByDecisionId"/> is null when nothing replaced it — a decision that was
/// simply overruled, or one whose replacement has not been recorded. An honest absence rather
/// than a pointer at the nearest plausible decision.
/// </para>
/// </summary>
public sealed record DecisionSuperseded(
    Guid Id,
    Guid? SupersededByDecisionId,
    string Reason,
    Guid SupersededByOwnerId,
    DateTimeOffset SupersededAt);
