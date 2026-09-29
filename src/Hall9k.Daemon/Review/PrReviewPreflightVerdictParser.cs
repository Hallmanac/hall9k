namespace Hall9k.Daemon.Review;

/// <summary>
/// Parses the pull-request review pre-flight's own terminal marker (idea 6be68ee2, finding 1,
/// phase one): <c>PREFLIGHT: safe|unsafe - &lt;reason&gt;</c>, reusing
/// <see cref="ReviewResultParser.LastMarkerValue"/> for the identical last-marker-wins tolerance
/// <see cref="ReviewResultParser.ParseVerdict"/> already gives its own marker. Matched by exact
/// token, not <c>.Contains</c>, because "unsafe" contains "safe" as a substring — the one hazard
/// <see cref="ReviewResultParser.ParseVerdict"/>'s own <c>.Contains</c> dispatch happens not to hit
/// (its two answers, "merge-ready" and "needs-fixes", do not collide) but this marker's two
/// answers do. Anything the marker's own value does not read as exactly "safe" is unsafe,
/// including a missing marker entirely: a pre-flight that cannot be read as safe is never treated
/// as one.
/// </summary>
public static class PrReviewPreflightVerdictParser
{
    public const string Marker = "PREFLIGHT:";

    public static PrReviewPreflightVerdict Parse(string? summary)
    {
        string? raw = ReviewResultParser.LastMarkerValue(summary, Marker);
        if (raw is null)
        {
            return new PrReviewPreflightVerdict(
                Safe: false, Verdict: string.Empty,
                Reason: "no PREFLIGHT marker was found in the session's own output");
        }

        // The marker's own first word, never a split on the first '-' — the prompt this parses
        // (PrReviewPreflightPromptBuilder) tells the session exactly that: "anything other than
        // exactly 'safe' or 'unsafe' as the marker's own first word is treated as unsafe." Splitting
        // on '-' instead let a hedge like "safe-ish - only a workflow comment changed" or
        // "safe-but-unsure" read its verdict token as exactly "safe" (independent pre-PR review,
        // cycle 3, both lenses).
        string trimmed = raw.Trim();
        int whitespaceIndex = trimmed.IndexOfAny([' ', '\t']);
        string verdictToken = (whitespaceIndex >= 0 ? trimmed[..whitespaceIndex] : trimmed).Trim();
        string rest = (whitespaceIndex >= 0 ? trimmed[(whitespaceIndex + 1)..] : string.Empty).Trim();
        string reason = rest.StartsWith('-') ? rest[1..].Trim() : rest;

        bool safe = string.Equals(verdictToken, "safe", StringComparison.OrdinalIgnoreCase);
        bool isUnsafe = string.Equals(verdictToken, "unsafe", StringComparison.OrdinalIgnoreCase);
        if (!safe && !isUnsafe)
        {
            return new PrReviewPreflightVerdict(
                Safe: false, Verdict: trimmed,
                Reason: $"the PREFLIGHT marker's own value ('{trimmed}') is neither 'safe' nor 'unsafe' — "
                    + "unparseable, and anything unparseable is unsafe");
        }

        return new PrReviewPreflightVerdict(safe, verdictToken.ToLowerInvariant(), reason);
    }
}

/// <summary>What a pre-flight's own marker said, parsed. <see cref="Reason"/> is the session's own stated reason, or an explanation of why it could not be read at all.</summary>
public sealed record PrReviewPreflightVerdict(bool Safe, string Verdict, string Reason);
