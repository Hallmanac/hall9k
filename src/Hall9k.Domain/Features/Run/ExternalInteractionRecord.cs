namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One <see cref="Events.ExternalInteractionLogged"/> event, as <see cref="Projections.RunDetails"/>
/// carries it forward — the projected read model <see cref="Hall9k.Daemon.Review.ReviewEngine"/>
/// queries by task rather than replaying every run's own event stream, the same shape
/// <see cref="ReviewParkResolution"/> already is for a settled review ruling.
/// </summary>
/// <param name="LoggedAt">When the command recorded it.</param>
/// <param name="Party">Who or what outside the session was interacted with, in the agent's own words.</param>
/// <param name="Summary">What happened, in the agent's own words.</param>
/// <param name="HumanDirected">Whether a human, not the agent's own judgment, directed the interaction or its outcome.</param>
/// <param name="Reason">The human's own instruction or reason, when <see cref="HumanDirected"/> is true.</param>
public sealed record ExternalInteractionRecord(
    DateTimeOffset LoggedAt,
    string Party,
    string Summary,
    bool HumanDirected,
    string? Reason);
