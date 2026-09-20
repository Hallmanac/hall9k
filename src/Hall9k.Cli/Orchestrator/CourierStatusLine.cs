using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Courier;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The feed courier's own line on <c>h9k status</c> (idea 89471598, piece 3): whether one is in
/// flight right now for this project, and when one last actually delivered. Null when this
/// project has never had a courier run at all — the same "a quiet pane says nothing" posture
/// <see cref="OrchestratorPresenceLine"/> holds for a project that has never registered a window.
/// </summary>
public static class CourierStatusLine
{
    /// <param name="inFlight">The most recent courier run for this project, when it is still running (<c>CompletedAt</c> null) — null otherwise.</param>
    /// <param name="lastDeliveredAt">When a courier for this project last actually delivered, across every run ever recorded — null if none ever has.</param>
    public static string? Describe(CourierRunDetails? inFlight, DateTimeOffset? lastDeliveredAt, DateTimeOffset now)
    {
        if (inFlight is null && lastDeliveredAt is null)
        {
            return null;
        }

        string flight = inFlight is { } running
            ? $"in flight (dispatched {DurationFormat.Short(Elapsed(running.DispatchedAt, now))} ago)"
            : "idle";
        string delivery = lastDeliveredAt is { } deliveredAt
            ? $"last delivered {deliveredAt.ToLocalTime():yyyy-MM-dd HH:mm} ({DurationFormat.Short(Elapsed(deliveredAt, now))} ago)"
            : "never delivered";
        return $"courier: {flight}; {delivery}";
    }

    /// <summary>Clamped at zero — the identical reason <see cref="OrchestratorPresenceLine"/>'s own copy of this clamp exists.</summary>
    private static TimeSpan Elapsed(DateTimeOffset since, DateTimeOffset now) =>
        now > since ? now - since : TimeSpan.Zero;
}
