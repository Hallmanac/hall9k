namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// One agent CLI's own way of carrying a courier's message into an already-running orchestrator
/// session (idea 89471598, piece 3). Claude Code's own cross-session mesh (<c>ListAgents</c>/
/// <c>SendMessage</c>, the same mechanism outbound milestones already address a human's
/// registered session through) is the only one that exists today; another vendor's CLI, when one
/// is registered, gets its own implementation rather than this one guessing at a mechanism it has
/// never been told about.
/// </summary>
public interface ICourierDeliveryAdapter
{
    /// <summary>The <c>--cli</c> value <c>h9k orchestrator register</c> records this adapter for (case-insensitive).</summary>
    string Cli { get; }

    /// <summary>
    /// The delivery instructions this adapter's own mechanism needs, appended to the courier's
    /// prompt after the message to deliver. Tells the session how to address
    /// <paramref name="sessionName"/> and exactly how to report back whether the send landed —
    /// <see cref="CourierPromptBuilder.DeliveredMarker"/>/<see cref="CourierPromptBuilder.FailedMarkerPrefix"/>
    /// — since that marker, not anything else the session says, is what the daemon reads back to
    /// decide whether to drain the feed.
    /// </summary>
    string BuildDeliveryInstruction(string sessionName);
}
