namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One <see cref="Events.RunPhaseDelegated"/> event, as <see cref="Projections.RunDetails"/>
/// carries it forward — mirrors <see cref="ExternalInteractionRecord"/>'s own shape for the
/// identical reason: a projected read model rather than a stream replay for anything that needs
/// this history (<c>LogsCommand</c>, <c>HeadlessTokenRecovery.AppendDelegatedPhaseTokens</c>,
/// <c>TaskDeliverCommand</c>'s own recovered-handoff read).
/// </summary>
/// <param name="DelegatedAt">When the operator dispatched the contractor.</param>
/// <param name="Note">The operator's own handoff note, verbatim.</param>
/// <param name="DelegatedByOwnerId">Who delegated it.</param>
/// <param name="SessionName">The contractor session's own slice-1 mesh name — identical across every delegation on this run.</param>
/// <param name="SessionFileKey">
/// Unique per delegation — what <c>RunPaths.Session*File</c> is actually keyed on, since
/// <paramref name="SessionName"/> alone collides across a run delegated more than once
/// (independent pre-PR review, cycle 1, both lenses).
/// </param>
public sealed record PhaseDelegation(
    DateTimeOffset DelegatedAt, string Note, Guid DelegatedByOwnerId, string SessionName, string SessionFileKey);
