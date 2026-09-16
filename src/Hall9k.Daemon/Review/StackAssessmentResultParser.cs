namespace Hall9k.Daemon.Review;

/// <summary>
/// Tolerant reader of the stack assessment run's own trailer (task: a stacked checkpoint that
/// would park for a human on a git shape first dispatches a read-only assessment run), the same
/// "last marker line wins" discipline <see cref="ReviewResultParser"/> already applies to
/// <c>VERDICT:</c>/<c>RESOLUTION:</c>. A missing or malformed trailer is undecidable by design —
/// there is nothing here for the caller to distinguish "the agent said undecidable" from "the
/// agent's answer could not be read", and treating the two the same is what makes this parser
/// honest: it never invents a boundary or an onto commit an assessment did not actually name.
/// </summary>
internal static class StackAssessmentResultParser
{
    internal const string VerdictMarker = "STACK ASSESSMENT VERDICT:";
    internal const string BoundaryMarker = "BOUNDARY:";
    internal const string OntoMarker = "ONTO:";
    internal const string EvidenceMarker = "EVIDENCE:";

    public static StackAssessmentVerdict Parse(string? summary)
    {
        if (summary.IsBlank())
        {
            return StackAssessmentVerdict.Undecidable(
                "the assessment run produced no summary at all, so no STACK ASSESSMENT VERDICT trailer could be read");
        }

        string? verdictValue = LastMarkerValue(summary, VerdictMarker);
        string? boundary = LastMarkerValue(summary, BoundaryMarker);
        string? onto = LastMarkerValue(summary, OntoMarker);
        string evidence = EvidenceBlock(summary);

        if (verdictValue.IsBlank())
        {
            return StackAssessmentVerdict.Undecidable(
                Malformed("carried no STACK ASSESSMENT VERDICT: line", evidence));
        }

        return verdictValue.Trim().ToLowerInvariant() switch
        {
            "aligned" => boundary.IsNotBlank() && onto.IsNotBlank() && evidence.IsNotBlank()
                ? StackAssessmentVerdict.Aligned(boundary.Trim(), onto.Trim(), evidence)
                : StackAssessmentVerdict.Undecidable(Malformed(
                    "declared aligned without a well-formed BOUNDARY:, ONTO:, and EVIDENCE: block", evidence)),
            "replay" => boundary.IsNotBlank() && onto.IsNotBlank() && evidence.IsNotBlank()
                ? StackAssessmentVerdict.Replay(boundary.Trim(), onto.Trim(), evidence)
                : StackAssessmentVerdict.Undecidable(Malformed(
                    "declared replay without a well-formed BOUNDARY:, ONTO:, and EVIDENCE: block", evidence)),
            "undecidable" => StackAssessmentVerdict.Undecidable(
                evidence.IsNotBlank() ? evidence : Malformed("declared undecidable with no EVIDENCE: block", evidence)),
            _ => StackAssessmentVerdict.Undecidable(
                Malformed($"carried an unrecognized verdict '{verdictValue.Trim()}'", evidence)),
        };
    }

    private static string Malformed(string what, string evidenceSoFar) => evidenceSoFar.IsNotBlank()
        ? $"the assessment run's trailer was malformed: it {what}. What evidence text it did carry: {evidenceSoFar}"
        : $"the assessment run's trailer was malformed: it {what}.";

    private static string? LastMarkerValue(string? summary, string marker)
    {
        if (summary.IsBlank())
        {
            return null;
        }

        string? value = null;
        foreach (string rawLine in summary.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                value = line[marker.Length..].Trim();
            }
        }

        return value;
    }

    private static string EvidenceBlock(string summary)
    {
        int index = summary.LastIndexOf(EvidenceMarker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? string.Empty : summary[(index + EvidenceMarker.Length)..].Trim();
    }
}
