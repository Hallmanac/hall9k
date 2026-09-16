using System.Text.RegularExpressions;

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
internal static partial class StackAssessmentResultParser
{
    internal const string VerdictMarker = "STACK ASSESSMENT VERDICT:";
    internal const string BoundaryMarker = "BOUNDARY:";
    internal const string OntoMarker = "ONTO:";
    internal const string EvidenceMarker = "EVIDENCE:";

    /// <summary>
    /// What <see cref="BoundaryMarker"/> and <see cref="OntoMarker"/> are allowed to hold for an
    /// <c>aligned</c> or <c>replay</c> verdict: a git commit SHA, 7 to 40 hex characters, and
    /// nothing else — never the literal <c>none</c> the template shows only for
    /// <c>undecidable</c>, never a branch name, and never a value that could reach a git
    /// subcommand as an option rather than a revision (independent pre-PR review, cycle 1,
    /// adversarial lens: the shape this guards against is `git rebase --onto &lt;a value starting
    /// with `-`&gt; ...`). This is a format check only — it says nothing about whether the commit
    /// actually exists in this repository, which is <see cref="ReviewEngine.RecordStackAssessmentCompletedAsync"/>'s
    /// own job.
    /// </summary>
    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitShaPattern();

    private static bool LooksLikeCommitSha(string value) => CommitShaPattern().IsMatch(value.Trim());

    public static StackAssessmentVerdict Parse(string? summary)
    {
        if (summary.IsBlank())
        {
            return StackAssessmentVerdict.Undecidable(
                "the assessment run produced no summary at all, so no STACK ASSESSMENT VERDICT trailer could be read");
        }

        // The trailer contract's own fixed order (stack-assessment.md) puts EVIDENCE: last, so the
        // marker search is bounded to everything before it — otherwise a line inside the evidence
        // block itself that happens to start with one of these markers (an agent quoting a
        // rejected candidate, e.g. "Onto: <sha the agent ruled out>") would be read as the LAST,
        // and therefore winning, match under the tolerant "last marker wins" rule below
        // (independent pre-PR review, cycle 1, conformance lens).
        int evidenceMarkerIndex = summary.LastIndexOf(EvidenceMarker, StringComparison.OrdinalIgnoreCase);
        string trailerSection = evidenceMarkerIndex < 0 ? summary : summary[..evidenceMarkerIndex];

        string? verdictValue = LastMarkerValue(trailerSection, VerdictMarker);
        string? boundary = LastMarkerValue(trailerSection, BoundaryMarker);
        string? onto = LastMarkerValue(trailerSection, OntoMarker);
        string evidence = EvidenceBlock(summary);

        if (verdictValue.IsBlank())
        {
            return StackAssessmentVerdict.Undecidable(
                Malformed("carried no STACK ASSESSMENT VERDICT: line", evidence));
        }

        return verdictValue.Trim().ToLowerInvariant() switch
        {
            "aligned" => boundary.IsNotBlank() && onto.IsNotBlank() && LooksLikeCommitSha(boundary)
                    && LooksLikeCommitSha(onto) && evidence.IsNotBlank()
                ? StackAssessmentVerdict.Aligned(boundary.Trim(), onto.Trim(), evidence)
                : StackAssessmentVerdict.Undecidable(Malformed(
                    "declared aligned without a well-formed BOUNDARY:, ONTO:, and EVIDENCE: block — BOUNDARY: and "
                    + "ONTO: must each be a commit SHA, never \"none\" or a branch name", evidence)),
            "replay" => boundary.IsNotBlank() && onto.IsNotBlank() && LooksLikeCommitSha(boundary)
                    && LooksLikeCommitSha(onto) && evidence.IsNotBlank()
                ? StackAssessmentVerdict.Replay(boundary.Trim(), onto.Trim(), evidence)
                : StackAssessmentVerdict.Undecidable(Malformed(
                    "declared replay without a well-formed BOUNDARY:, ONTO:, and EVIDENCE: block — BOUNDARY: and "
                    + "ONTO: must each be a commit SHA, never \"none\" or a branch name", evidence)),
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
