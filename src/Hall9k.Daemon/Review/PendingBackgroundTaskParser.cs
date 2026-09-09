using System.Text.RegularExpressions;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Recognizes a fix session's own final message naming a background task it is still waiting on
/// (task: a headless build, fix, or recovery session never ends its turn while a gate it started
/// is still running in the background) — the shape all three origin incidents took, each ending a
/// turn with a `dotnet test` run backgrounded rather than waited on in the foreground: "I'm
/// waiting on the background dotnet test run... to complete before finishing this session", "The
/// full dotnet test run is still running in the background", "Test suite is running in the
/// background; I've set a monitor to notify me." All three name the word "background" together
/// with a word for the task still being in flight — matched narrowly on both, per the
/// never-guess-at-unobserved-facts discipline (AGENTS.md), rather than on "background" alone: an
/// innocuous "for background context, this fixes X" carries the first word without the second and
/// must not be misread as a policy violation it never was.
/// <para>
/// Feeds <c>ReviewResultParser.ParseFixOutcome</c>'s fallback when no <c>RESOLUTION:</c> marker is
/// found, so a session that says this reads as
/// <see cref="Hall9k.Domain.Features.Run.ReviewFixOutcome.WaitingOnBackgroundGate"/> — a distinct,
/// named outcome — rather than the generic <c>(undeclared)</c> every other unmarked ending shares
/// (h9k task show and the run log both name the cause instead of reporting it as undeclared).
/// </para>
/// </summary>
public static class PendingBackgroundTaskParser
{
    private static readonly string[] StillInFlightWords =
        ["running", "wait", "monitor", "finish", "complete"];

    private static readonly char[] ClauseSeparators = ['.', '!', '?', ';', '\n'];

    /// <summary>
    /// Phrases that turn "background" from something still pending into something explicitly
    /// avoided or already resolved: "rather than backgrounding it", "instead of the background
    /// run" — neither carries one of the standalone negation words below, so each is matched as
    /// its own fixed cue instead. Checked within the sub-clause naming "background", or a later
    /// one in the same clause (see <see cref="HasNegationCue"/>), not the whole summary, so a
    /// negation elsewhere in a long summary cannot silently suppress a genuine pending-task clause
    /// later on.
    /// </summary>
    private static readonly string[] NegationPhraseCues = ["rather than", "instead of"];

    /// <summary>
    /// Standalone negation words, matched on word boundaries so "no"/"not"/etc. inside an
    /// unrelated word (e.g. "ignore", "monitor", "notify") never counts. Covers any clause that
    /// denies a pending background task in its own words — "no background monitors were set",
    /// "nothing is left running in the background", "no build or test was left running in the
    /// background", "I am not waiting on anything in the background" — rather than only the
    /// handful of fixed phrasings a session happens to reuse verbatim: the second and fourth of
    /// those are exactly how a compliant session naturally affirms it left nothing behind, and a
    /// cue list narrow enough to miss them reads that affirmation back as the violation it denies
    /// (independent pre-PR review, cycle 1, adversarial lens). "n't" is checked separately since a
    /// contraction like "isn't"/"wasn't" has no word boundary before its own "n't" for the regex
    /// to anchor on.
    /// <para>
    /// This pattern alone is not enough to decide a negation applies: <see cref="HasNegationCue"/>
    /// only counts a match found in the sub-clause naming "background" or a later one (see that
    /// method's own remarks) — a negation word in an earlier, unrelated sub-clause ("There's no way
    /// to shorten the test run, so it's still running in the background..."), must not suppress a
    /// genuine still-in-flight mention later in the same period-delimited clause (independent
    /// pre-PR review, cycle 2, adversarial lens).
    /// </para>
    /// </summary>
    private static readonly Regex NegationWordPattern = new(
        @"\b(no|not|nothing|never|without)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits a clause into narrower sub-clauses at commas and at coordinating/subordinating
    /// conjunctions that typically introduce a new independent statement ("so", "but", "while",
    /// ...). Used only to scope negation matching (see <see cref="HasNegationCue"/>): a negation
    /// earlier in the sentence should not reach across one of these boundaries to suppress a
    /// pending-task claim in a later, unrelated part of the same sentence. Deliberately excludes
    /// "and"/"or" — both commonly conjoin two verb phrases describing the same ongoing action
    /// ("no build or test was left running") rather than starting an unrelated statement, and
    /// splitting on them would reintroduce the same false-negative risk this pattern exists to
    /// avoid.
    /// </summary>
    private static readonly Regex SubClauseSeparatorPattern = new(
        @",|\b(?:so|but|yet|while|because|since|although|though)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool NamesPendingBackgroundTask(string? summary)
    {
        if (summary is null)
        {
            return false;
        }

        foreach (string clause in summary.Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!clause.Contains("background", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (HasNegationCue(clause))
            {
                continue;
            }

            if (StillInFlightWords.Any(word => clause.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a sub-clause that opens by referring back to something named earlier rather than
    /// introducing a new subject of its own: a relative pronoun ("which", "who", "whose", "that")
    /// or a bare "it". Used to keep <see cref="HasNegationCue"/> walking forward past the
    /// "background"-naming sub-clause only while each next one is still talking about the same
    /// background task by pronoun, e.g. "a background test, which never finished" — never past a
    /// sub-clause that opens a new, unrelated statement with its own subject.
    /// </summary>
    private static readonly Regex BackReferringPronounPattern = new(
        @"^\s*(which|who|whose|that|it)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True only when a negation is found in the sub-clause (see
    /// <see cref="SubClauseSeparatorPattern"/>) that first names "background", or in a run of
    /// immediately-following sub-clauses that each keep referring back to it by pronoun rather than
    /// introducing a new subject — never in one before it, and never past the first sub-clause that
    /// moves on to something else. A later sub-clause can deny the earlier one by pronoun rather
    /// than repeating "background" itself ("a background test, which never finished, so I killed
    /// it" — "never" lands in the sub-clause right after the one naming "background", denying it by
    /// referring back to "which"), so scoping to only the "background"-naming sub-clause itself
    /// would miss it. But a sub-clause that instead opens with its own new subject denies something
    /// else entirely, even when it shares the sentence: "the background build is still running, but
    /// I don't expect it to fail" denies an expectation about failure, not whether the build is
    /// still pending, so its "don't" must not suppress the genuine claim named earlier in the same
    /// sentence (independent pre-PR review, cycle 5, adversarial lens — the prior fix scanned every
    /// sub-clause through the end of the sentence once one of them named "background", letting an
    /// unrelated negation anywhere later in the sentence suppress a real pending-task claim). A
    /// sub-clause *before* the one naming "background" is excluded even when it carries a negation
    /// word, because that negation has nothing yet to deny: "I don't know how long this will take to
    /// complete, but the background test is still running" must not have its earlier, unrelated
    /// "don't" suppress the later sub-clause that actually names the pending background task
    /// (independent pre-PR review, cycle 3, adversarial lens).
    /// </summary>
    private static bool HasNegationCue(string clause)
    {
        string[] segments = SubClauseSeparatorPattern.Split(clause);

        int backgroundIndex = Array.FindIndex(
            segments, segment => segment.Contains("background", StringComparison.OrdinalIgnoreCase));

        if (backgroundIndex < 0)
        {
            return false;
        }

        for (int i = backgroundIndex; i < segments.Length; i++)
        {
            string segment = segments[i];

            if (i > backgroundIndex && string.IsNullOrWhiteSpace(segment))
            {
                // An artifact of two adjacent separator matches (e.g. ", but"), not a sub-clause
                // of its own — skip it without treating it as an unrelated new subject.
                continue;
            }

            if (i > backgroundIndex && !BackReferringPronounPattern.IsMatch(segment))
            {
                break;
            }

            if (NegationWordPattern.IsMatch(segment)
                || segment.Contains("n't", StringComparison.OrdinalIgnoreCase)
                || NegationPhraseCues.Any(cue => segment.Contains(cue, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
