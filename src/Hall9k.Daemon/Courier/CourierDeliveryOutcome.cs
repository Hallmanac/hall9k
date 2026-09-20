using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Courier;

namespace Hall9k.Daemon.Courier;

/// <summary>
/// Reads a courier session's own terminal result for <see cref="CourierPromptBuilder.DeliveredMarker"/>
/// (idea 89471598, piece 3) — pulled out of <see cref="CourierEngine"/> so it is a pure function a
/// test can call directly, with no store, no process, and no session behind it.
/// </summary>
public static class CourierDeliveryOutcome
{
    public static (bool Delivered, string Outcome) Parse(AgentResult? result, bool timedOut, TimeSpan timeout)
    {
        bool delivered = result?.Summary is { } summary
            && summary.Contains(CourierPromptBuilder.DeliveredMarker, StringComparison.Ordinal);
        string outcome = delivered
            ? "Delivered."
            : timedOut
                ? $"The session exceeded {timeout} and was stopped without a delivered marker."
                : result?.Summary is { Length: > 0 } said
                    ? $"The session ended without a delivered marker. It said: {said}"
                    : "The session ended without a delivered marker and left no result to read.";
        return (delivered, outcome);
    }
}
