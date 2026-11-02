using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Courier;

namespace Hall9k.Daemon.Courier;

/// <summary>
/// Reads a courier session's own terminal result for <see cref="CourierPromptBuilder.DeliveredMarker"/>
/// (idea 89471598, piece 3) — pulled out of <see cref="CourierEngine"/> so it is a pure function a
/// test can call directly, with no store, no process, and no session behind it.
/// <para>
/// Only a line that starts with the shared <c>COURIER-OUTCOME:</c> prefix counts, and the last
/// such line wins — the identical discipline <c>ReviewResultParser.LastMarkerValue</c> already
/// applies to its own VERDICT/RESOLUTION lines, for the same observed reason: a session sometimes
/// quotes its own instructions ("I was asked to end with `COURIER-OUTCOME: delivered` if …") before
/// answering, and a bare substring search over the whole summary would read that quoted mention as
/// the verdict even where the session's real, standalone final line reports a failure (independent
/// pre-PR review, cycle 4, conformance and adversarial lenses).
/// </para>
/// </summary>
public static class CourierDeliveryOutcome
{
    private const string MarkerPrefix = "COURIER-OUTCOME:";

    public static (bool Delivered, string Outcome) Parse(AgentResult? result, bool timedOut, TimeSpan timeout)
    {
        string? marker = LastMarkerLine(result?.Summary);
        bool delivered = marker is not null
            && marker.StartsWith(CourierPromptBuilder.DeliveredMarker, StringComparison.Ordinal);
        string outcome = delivered
            ? "Delivered."
            : timedOut
                ? $"The session exceeded {timeout} and was stopped without a delivered marker."
                : result?.Summary is { Length: > 0 } said
                    ? $"The session ended without a delivered marker. It said: {said}"
                    : "The session ended without a delivered marker and left no result to read.";
        return (delivered, outcome);
    }

    /// <summary>
    /// Why a courier that did not deliver failed, in the words <c>h9k status</c> and
    /// <c>h9k doctor</c> show: the text after <c>COURIER-OUTCOME: failed -</c> when the session gave
    /// one, otherwise "no reason given" (the marker with nothing after it), "timed out after 3m",
    /// or "ended without an outcome line" (no marker, or one that is neither delivered nor failed).
    /// Raw: the stored form is <see cref="CourierFailureStreak.NormalizeReason"/>'s, and the run's
    /// own <c>Outcome</c> is left as <see cref="Parse"/> wrote it.
    /// </summary>
    public static string FailureReason(AgentResult? result, bool timedOut, TimeSpan timeout)
    {
        if (timedOut)
        {
            return $"timed out after {FormatTimeout(timeout)}";
        }

        string? marker = LastMarkerLine(result?.Summary);
        if (marker is null || !marker.StartsWith(CourierPromptBuilder.FailedMarkerPrefix, StringComparison.Ordinal))
        {
            return CourierFailureStreak.EndedWithoutOutcomeLine;
        }

        string reason = marker[CourierPromptBuilder.FailedMarkerPrefix.Length..].TrimStart().TrimStart('-', '\u2013', '\u2014').Trim();
        return reason.Length == 0 ? CourierFailureStreak.NoReasonGiven : reason;
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds % 60 == 0 && timeout.TotalMinutes >= 1
            ? $"{(int)timeout.TotalMinutes}m"
            : $"{(int)timeout.TotalSeconds}s";

    private static string? LastMarkerLine(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return null;
        }

        string? found = null;
        foreach (string rawLine in summary.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                found = trimmed;
            }
        }

        return found;
    }
}
