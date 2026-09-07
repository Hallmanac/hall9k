using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Tolerant reader of the review loop's marker lines (Decisions Log #23). The reviewer
/// ends with "VERDICT: merge-ready | needs-fixes", the fix session with
/// "RESOLUTION: fixed | disputed". The last marker in the summary wins (agents sometimes
/// quote the instructions before answering); anything unparseable maps to the Unknown
/// sentinel — the engine decides what honesty requires, never this parser.
/// <para>
/// The adversarial pass additionally opens each finding with a "FINDING:" header carrying its
/// grade and scope tag (Decisions Log #63), which <see cref="ParseFindings"/> reads. Same
/// discipline: an absent or unrecognized tag becomes Unknown here, and the gate — not this
/// parser — decides what an ungraded finding costs.
/// </para>
/// </summary>
public static class ReviewResultParser
{
    /// <summary>The header line that opens one structured finding block.</summary>
    public const string FindingMarker = "FINDING:";

    /// <summary>
    /// The exact `at=` value in the finding contract's own worked example
    /// (<c>AgentPromptBuilder.AppendFindingContract</c>). A pass that quotes the contract before
    /// answering — the same observed habit <see cref="LastMarkerValue"/> already tolerates for
    /// VERDICT — echoes this header back verbatim; unlike a verdict line, where the last one
    /// wins, a finding block has no "last one wins" rule, so an echoed example would otherwise
    /// stand as a second, fabricated finding alongside whatever the pass actually reported.
    /// <see cref="Close"/> drops any block whose location's path matches this placeholder's path
    /// — the same path-first check <see cref="ReviewVerdictValidation.IsPlaceholderLocation"/>
    /// uses, not an exact match against the full `path:line` string (cycle-3 adversarial
    /// finding): a pass that drops or adapts the line number while echoing this example still
    /// points at a path no repository has, and an exact-literal comparison let that survive as a
    /// fabricated finding rather than dispatching a fix session at a file that was never touched.
    /// </summary>
    public const string ExampleLocationPlaceholder = "src/Some/File.cs:123";

    private const string VerdictMarker = "VERDICT:";

    /// <summary>
    /// The structured findings in a review pass's output, in the order they were written. A
    /// block runs from its FINDING header to the next header or the verdict line, and its text
    /// is carried whole so the fix session and any routed draft read exactly what the reviewer
    /// wrote rather than a reconstruction of it.
    /// <para>
    /// Returns empty when the output carries no headers at all. That is not "no findings" — it
    /// is "no findings this parser can read", and the caller is the one that knows whether the
    /// verdict said there were any.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReviewFinding> ParseFindings(string? summary)
    {
        if (summary.IsBlank())
        {
            return [];
        }

        List<ReviewFinding> findings = [];
        List<string>? block = null;
        foreach (string rawLine in summary.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();
            bool opensFinding = trimmed.StartsWith(FindingMarker, StringComparison.OrdinalIgnoreCase);
            bool endsFindings = trimmed.StartsWith(VerdictMarker, StringComparison.OrdinalIgnoreCase);
            if (opensFinding || endsFindings)
            {
                Close(findings, block);
                block = opensFinding ? [trimmed] : null;
                continue;
            }

            block?.Add(line);
        }

        Close(findings, block);
        return findings;
    }

    private static void Close(List<ReviewFinding> findings, List<string>? block)
    {
        if (block is null)
        {
            return;
        }

        Dictionary<string, string> header = HeaderTags(block[0][FindingMarker.Length..]);
        string location = Tag(header, "at") ?? Tag(header, "file") ?? Tag(header, "location") ?? string.Empty;
        if (location.IsNotBlank() && ReviewVerdictValidation.IsPlaceholderLocation(location))
        {
            return;
        }

        findings.Add(new ReviewFinding(
            ReviewSeverity.Parse(Tag(header, "severity")),
            ReviewFindingScope.Parse(Tag(header, "scope")),
            location,
            string.Join('\n', block).Trim(),
            ParseTrack(Tag(header, "track"))));
    }

    /// <summary>
    /// The `track=` tag a <see cref="ReviewMode.Verify"/> pass's finding carries (task: review
    /// cycles after the first) — absent from a Discovery or FinalFullPass pass's findings, since
    /// those already know their own lens from the pass itself. Anything other than the two real
    /// lens names reads as null (applies to every still-active track), the same conservative
    /// default an absent tag gets — never guessed at.
    /// </summary>
    private static ReviewLens? ParseTrack(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "conformance" => ReviewLens.Conformance,
        "adversarial" => ReviewLens.Adversarial,
        _ => null,
    };

    /// <summary>
    /// The header's `key=value` tags, separated by semicolons or commas — both are accepted
    /// because both are what agents actually write, and a `file.cs:123` value contains neither.
    /// A repeated key keeps the first, which is the one the reviewer wrote first.
    /// </summary>
    private static Dictionary<string, string> HeaderTags(string header)
    {
        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in header.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = part[..separator].Trim();
            string value = part[(separator + 1)..].Trim().Trim('`');
            if (key.IsNotBlank() && value.IsNotBlank())
            {
                tags.TryAdd(key, value);
            }
        }

        return tags;
    }

    private static string? Tag(Dictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out string? value) ? value : null;

    /// <summary>The header line that opens one parked-disagreement block (task: a changes-requested pull-request review from a human becomes a fix lap).</summary>
    public const string DisagreementMarker = "DISAGREEMENT:";

    /// <summary>The sub-marker introducing what the reviewer asked for, in the session's own restatement.</summary>
    public const string ReviewerAskedMarker = "REVIEWER ASKED:";

    /// <summary>The sub-marker introducing the session's own position.</summary>
    public const string DisagreementReasoningMarker = "MY REASONING:";

    /// <summary>The sub-marker introducing the reply the session drafted for the implementer to send, edit, or drop.</summary>
    public const string ProposedReplyMarker = "PROPOSED REPLY:";

    /// <summary>
    /// The disagreements a changes-requested fix lap parked rather than answering itself (task: a
    /// changes-requested pull-request review from a human becomes a fix lap), in the order they
    /// were written. A block runs from its <see cref="DisagreementMarker"/> header to the next
    /// header, to the <c>RESOLUTION:</c> line, or to the <c>HANDOFF:</c> block — whichever comes
    /// first.
    /// <para>
    /// Tolerant in the same way <see cref="ParseFindings"/> is, and for the same reason: this
    /// parser never decides what honesty requires. A block missing a sub-marker yields a blank
    /// field rather than a guess, and a block whose prose carries no sub-markers at all is read
    /// whole as the session's reasoning — it plainly said something, and the only thing that
    /// cannot be recovered is which half of it was which. What that costs is visible to the human
    /// resolving the park, which is exactly who should be the one to notice.
    /// </para>
    /// <para>
    /// Returns empty when the output carries no headers at all. That is not "the session agreed
    /// with everything" — <c>ReviewFixOutcome.Disputed</c> is what says a disagreement exists, and
    /// the park is recorded on the strength of that marker whether or not a single block here
    /// parses.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReviewDisagreement> ParseDisagreements(string? summary)
    {
        if (summary.IsBlank())
        {
            return [];
        }

        List<ReviewDisagreement> disagreements = [];
        List<string>? block = null;
        foreach (string rawLine in summary.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();
            bool opensBlock = trimmed.StartsWith(DisagreementMarker, StringComparison.OrdinalIgnoreCase);
            bool endsBlocks = trimmed.StartsWith("RESOLUTION:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("HANDOFF:", StringComparison.OrdinalIgnoreCase);
            if (opensBlock || endsBlocks)
            {
                CloseDisagreement(disagreements, block);
                block = opensBlock ? [trimmed] : null;
                continue;
            }

            block?.Add(line);
        }

        CloseDisagreement(disagreements, block);
        return disagreements;
    }

    private static void CloseDisagreement(List<ReviewDisagreement> disagreements, List<string>? block)
    {
        if (block is null)
        {
            return;
        }

        Dictionary<string, string> header = HeaderTags(block[0][DisagreementMarker.Length..]);
        // The same three spellings ParseFindings accepts for a location, for the same reason: this
        // is one contract written twice, and a session that reaches for the finding contract's own
        // `file=` here should not lose its location for it.
        string? location = Tag(header, "at") ?? Tag(header, "file") ?? Tag(header, "location");

        // The same echoed-example guard <see cref="Close"/> applies to a finding, and it matters
        // more here (self-review, this task): a session that quotes the contract before answering
        // — the observed habit LastMarkerValue already tolerates for VERDICT — would otherwise
        // park a run over a file no repository has, carrying a placeholder "proposed reply" that
        // h9k review resolve would offer to send to a real reviewer.
        if (location.IsNotBlank() && ReviewVerdictValidation.IsPlaceholderLocation(location))
        {
            return;
        }

        (string finding, string reasoning, string reply) = SplitDisagreementBody(block.Skip(1));
        disagreements.Add(new ReviewDisagreement(
            finding, reasoning, reply,
            Location: location.IsBlank() ? null : location,
            ThreadId: Tag(header, "thread"),
            ReviewUrl: Tag(header, "review")));
    }

    /// <summary>
    /// One block's prose split at its sub-markers. Text ahead of the first sub-marker is folded
    /// into the reasoning: it is the session talking about its own position, and dropping it would
    /// hide part of what the implementer is being asked to weigh.
    /// </summary>
    private static (string Finding, string Reasoning, string ProposedReply) SplitDisagreementBody(
        IEnumerable<string> lines)
    {
        List<string> finding = [];
        List<string> reasoning = [];
        List<string> reply = [];
        List<string> current = reasoning;
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (StartsSection(trimmed, ReviewerAskedMarker, out string remainder))
            {
                current = finding;
            }
            else if (StartsSection(trimmed, DisagreementReasoningMarker, out remainder))
            {
                current = reasoning;
            }
            else if (StartsSection(trimmed, ProposedReplyMarker, out remainder))
            {
                current = reply;
            }
            else
            {
                current.Add(line);
                continue;
            }

            if (remainder.IsNotBlank())
            {
                current.Add(remainder);
            }
        }

        return (Join(finding), Join(reasoning), Join(reply));

        static bool StartsSection(string trimmed, string marker, out string remainder)
        {
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                remainder = trimmed[marker.Length..].Trim();
                return true;
            }

            remainder = string.Empty;
            return false;
        }

        static string Join(List<string> lines) => string.Join('\n', lines).Trim();
    }

    public static ReviewVerdict ParseVerdict(string? summary) =>
        LastMarkerValue(summary, "VERDICT:") switch
        {
            { } value when value.Contains("merge-ready", StringComparison.OrdinalIgnoreCase) => ReviewVerdict.MergeReady,
            { } value when value.Contains("needs-fixes", StringComparison.OrdinalIgnoreCase) => ReviewVerdict.NeedsFixes,
            _ => ReviewVerdict.Unknown,
        };

    public static ReviewFixOutcome ParseFixOutcome(string? summary) =>
        LastMarkerValue(summary, "RESOLUTION:") switch
        {
            { } value when value.Contains("disputed", StringComparison.OrdinalIgnoreCase) => ReviewFixOutcome.Disputed,
            { } value when value.Contains("fixed", StringComparison.OrdinalIgnoreCase) => ReviewFixOutcome.Fixed,
            _ => ReviewFixOutcome.Unknown,
        };

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
}
