namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One <see cref="Events.RunPhaseDelegated"/> event, as <see cref="Projections.RunDetails"/>
/// carries it forward — mirrors <see cref="ExternalInteractionRecord"/>'s own shape for the
/// identical reason: a projected read model rather than a stream replay for anything that needs
/// this history (a later review pass, <c>h9k task show</c>).
/// </summary>
/// <param name="DelegatedAt">When the operator dispatched the contractor.</param>
/// <param name="Note">The operator's own handoff note, verbatim.</param>
/// <param name="DelegatedByOwnerId">Who delegated it.</param>
/// <param name="SessionName">The contractor session's own slice-1 name.</param>
public sealed record PhaseDelegation(
    DateTimeOffset DelegatedAt, string Note, Guid DelegatedByOwnerId, string SessionName);
