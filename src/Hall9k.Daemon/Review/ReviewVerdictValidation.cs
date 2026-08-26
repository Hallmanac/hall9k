using System.Text.RegularExpressions;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Whether a needs-fixes verdict actually names something (origin: ten occurrences filed
/// 2026-08-25, two on 2026-08-23 — task 40's conformance lens issued needs-fixes over findings
/// it never enumerated, and task 24's adversarial lens issued a bare "VERDICT: needs-fixes"
/// with no finding text). A reviewer that claims needs-fixes without naming a location and a
/// defect has not been shown to have found anything, and recording that claim as though it were
/// a real one either parks a human over content that does not exist or spends a fix session
/// looking for a defect that was never described. This is the judgment call
/// <see cref="ReviewResultParser"/>'s own doctrine assigns to the engine, never the parser.
/// <para>
/// The check is deliberately lens-agnostic and format-agnostic: the adversarial lens's
/// <c>FINDING:</c> header and the conformance lens's plain-prose pointer (Decisions Log #63 —
/// conformance "grades nothing" and was never given the structured contract) both read as a
/// stated location the same way, because whether the reviewer used a header is not what this
/// checks — whether it named a place is. A verdict is validated on its raw output, before any
/// parsing decides what to do with what it found.
/// </para>
/// </summary>
public static partial class ReviewVerdictValidation
{
    private const string VerdictMarker = "VERDICT:";

    /// <summary>
    /// A location the way every finding in this codebase's review contract writes one: a file
    /// name with an extension, optionally followed by a line number (`Auth.cs`, `Auth.cs:42`,
    /// `src/Auth.cs:42`, `` `Auth.cs:42` ``), one of a fixed vocabulary of dotfiles this
    /// codebase's own tooling uses (`.gitignore`, `.gitattributes`, `.gitmodules`,
    /// `.dockerignore`, `.editorconfig`, `.env`, `.npmrc`, `.nvmrc`), or one of the handful of
    /// conventionally extensionless filenames it touches (`Dockerfile`, `Makefile`,
    /// `Jenkinsfile`, `Gemfile`, `Rakefile`, `Procfile`, `Vagrantfile`). The extension
    /// alternative requires at least two letters so an incidental "e.g." or "i.e." in a
    /// reviewer's prose is not read as a pointer into the diff, and the dotfile alternative is a
    /// fixed name list rather than a generic "dot followed by letters" pattern, because that
    /// generic shape also matches an ellipsis running into the next word ("hard...nothing") and
    /// prose like ".NET" or ".Where" that names nothing in the diff.
    /// </summary>
    [GeneratedRegex(
        @"[\w/\\-]*[A-Za-z0-9_-]\.[A-Za-z]{2,10}(:\d+)?"
        + @"|\B\.(?:gitignore|gitattributes|gitmodules|dockerignore|editorconfig|env|npmrc|nvmrc)(?::\d+)?\b"
        + @"|\b(?:Dockerfile|Makefile|Jenkinsfile|Gemfile|Rakefile|Procfile|Vagrantfile)(?::\d+)?\b")]
    private static partial Regex LocationPattern();

    /// <summary>
    /// The language a reviewer actually uses to say something is wrong, as opposed to a location
    /// that shows up only in passing — a doctrine citation ("against AGENTS.md") or an
    /// affirmation ("Program.cs proves it"). Deliberately narrow and literal (the same
    /// discipline as <see cref="Hall9k.Daemon.Execution.BudgetExhaustionParser"/>): this is not a
    /// semantic classifier, only a check for the vocabulary this codebase's own review contract's
    /// prose findings use, negation words and the affirmative defect verbs a finding is just as
    /// often stated with ("the token is dropped", "the manifest is overwritten") — the pre-PR
    /// review pass that added the affirmative half found a correctly located, correctly described
    /// finding rejected for using none of the (at the time, purely negation) list, which inverts
    /// this check's own purpose. The list grows opportunistically as a real reviewer's phrasing
    /// files a gap, the same way the codebase's other origin-incident vocabularies do; it is not
    /// meant to close on every way of saying something is wrong.
    /// <see cref="StructuralMarkerPattern"/> covers the structured contract's own "Defect:" /
    /// "Scenario:" labels, checked at paragraph scope instead of this pattern's sentence scope,
    /// because the two-line `FINDING:` block puts the label on the line after the location.
    /// </summary>
    [GeneratedRegex(
        @"\b(not|never|missing|fails?|failing|failed|wrong|incorrect|broken|defect|bug|cannot|can't|won't|"
        + @"doesn't|does not|didn't|no longer|without|unhandled|vulnerable|leaks?|crashes?|throws?|silently|"
        + @"drops?|dropped|overwrit(?:ten|es)|duplicat(?:es?|ed)|double-counts?|stale|ignor(?:es|ed)|"
        + @"skips?|skipped|corrupts?|corrupted|loses|lost|mismatch(?:ed)?|inconsistent|deadlocks?|hangs?|"
        + @"stuck|overflows?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DefectLanguagePattern();

    /// <summary>
    /// The structured review contract's own continuation labels, which introduce the defect
    /// description on the line after a `FINDING: … at=…` header rather than on the same sentence
    /// as the location — so this is checked once per paragraph, not per sentence.
    /// </summary>
    [GeneratedRegex(@"\b(?:Defect|Scenario):", RegexOptions.IgnoreCase)]
    private static partial Regex StructuralMarkerPattern();

    /// <summary>
    /// A blank line, the boundary between one finding's text and the next.
    /// </summary>
    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex ParagraphBoundary();

    /// <summary>
    /// A sentence boundary: end-of-sentence punctuation followed by whitespace and a capital
    /// letter. Deliberately does not fire on the period inside a filename (`Auth.cs proves`
    /// has no whitespace immediately after that period) so a location is never split from
    /// the clause that names it.
    /// </summary>
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex SentenceBoundary();

    /// <summary>
    /// A sentence's opening word when it visibly continues describing the subject the previous
    /// sentence introduced, rather than opening an unrelated claim of its own (found by the
    /// pre-PR review pass that hardened this check, filed 2026-08-25): a reviewer writing
    /// "`Auth.cs:42` is where the rate limiter lives. It is never reset …" puts the location in
    /// one sentence and the defect in the next, and the same-sentence-only rule rejected it. A
    /// short fixed list of backward-referencing pronouns, rather than "the next sentence,
    /// whatever it says": "Every criterion is met, and Program.cs proves it. Nothing here is
    /// wrong." also puts defect vocabulary in the sentence after a location, but "Nothing" opens
    /// a fresh, unrelated claim rather than continuing what the location's sentence said — the
    /// exact affirming-review shape this check exists to reject — so only a sentence that
    /// grammatically carries on from the location's own sentence is allowed to supply the
    /// defect language for it.
    /// </summary>
    [GeneratedRegex(@"^\s*(it|this|that|these|those|which)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationPattern();

    /// <summary>
    /// A sentence's opening words when the sentence itself is nothing but a pointer back at a
    /// location it names, rather than a claim about that location (cycle-6 finding,
    /// `ReviewVerdictValidation.cs:612`): a reviewer writing "The token is dropped when the loop
    /// exits early. See ReviewEngine.cs:612." states the defect first and the location second, so
    /// the location's own sentence carries no defect language and <see cref="ContinuationPattern"/>
    /// — which only ever looks forward — cannot recover it. Restricted to a fixed list of
    /// sentence-openers that are themselves defect-free pointers ("See", "This is at",
    /// "It is in") rather than "the previous sentence, whatever it says", the same discipline
    /// <see cref="ContinuationPattern"/> already applies looking forward: an affirming sentence
    /// like "Every criterion is met, and Program.cs proves it." does not open with one of these
    /// words, so it never borrows defect language from whatever came before it.
    /// <para>
    /// "Here" was dropped from this list (cycle-7 conformance finding,
    /// `ReviewVerdictValidation.cs:126`): unlike the other two, it routinely opens ordinary
    /// affirming or descriptive prose that is not a location pointer at all — "Nothing I checked
    /// failed. Here is what I read: `ReviewEngine.cs`." puts real defect language in the sentence
    /// before a plain summary sentence, and treating "Here" as a defect-free pointer wrongly
    /// borrowed that defect language for a location the summary sentence never actually
    /// implicated.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^\s*(see|this is at|it is in)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackwardPointerPattern();

    /// <summary>
    /// Whether a needs-fixes pass's own output states at least one finding, once the verdict
    /// line itself is set aside: a location the platform can point a human or a fix session at,
    /// paired with defect language close enough to it to plausibly describe what is wrong
    /// there — the same sentence, a sentence that visibly continues it, a sentence before it when
    /// the location's own sentence is only a backward pointer to it, or (for the structured
    /// contract's `Defect:`/`Scenario:` labels) the same paragraph. An output with nothing left
    /// after its verdict line, prose that never points anywhere concrete, or a location mentioned
    /// in one sentence with unrelated defect language confined to another, has not named
    /// anything — the two filed origin incidents (an unenumerated needs-fixes and a bare verdict)
    /// read this way, and so do the affirming-review and "findings reported above" shapes the
    /// pre-PR review pass that hardened this check surfaced as the same failure mode, without
    /// either having been filed as its own occurrence.
    /// <para>
    /// <see cref="ReviewResultParser.ParseFindings"/>'s own machine-readable read of a structured
    /// `FINDING:` block is the stronger signal and is trusted first, full stop, whenever it found
    /// one with a location on its own header tag and a defect stated beyond that header (see
    /// <see cref="HasStatedDefect"/>) — the block's header can carry a location the prose
    /// heuristic below would otherwise miss (its `Defect:`/`Scenario:` label pushed into its own
    /// paragraph by a blank line, or its body written as plain prose with no negation word at
    /// all). A block whose header tag the parser could not read (an unrecognized separator, or
    /// the location stated only in the `Defect:`/`Scenario:` body rather than the header) is not
    /// "no finding": the prose heuristic below still runs over the whole output, header text
    /// included, so a location sitting in that same text is not lost just because the stronger
    /// signal came back empty (adversarial cycle-4 finding, `ReviewVerdictValidation.cs:144`).
    /// </para>
    /// <para>
    /// This is a keyword-and-proximity check, not a semantic one, so it narrows rather than
    /// closes the gap between "the location is wrong" and "nothing about the location is
    /// wrong": an output whose own sentence negates the defect itself
    /// ("I did not find any defect in Foo.cs") still passes, because "not" and "defect" are
    /// both in this method's vocabulary and share that sentence with the location. Closing that
    /// gap needs reading comprehension a regex cannot do.
    /// </para>
    /// <para>
    /// Every branch below runs over <see cref="StripPlaceholderLocations"/>'s output, never the
    /// raw one (cycle-9 finding): screening for this file's own prompt placeholders used to be
    /// each branch's own job, and the structural-marker branch never did its share — a paragraph
    /// that echoed the finding contract's own worked example with anything extra appended around
    /// it (so the exact-match screen that used to guard only that branch could not fire) still
    /// tripped `Defect:`/`Scenario:` and named a finding against a path no repository has. Doing
    /// the screening once, before any branch runs, means no branch — present or a future one —
    /// can ever match a placeholder, because none survives long enough to be matched.
    /// </para>
    /// </summary>
    public static bool NamesAFinding(string? output)
    {
        if (output.IsBlank())
        {
            return false;
        }

        string sanitized = StripPlaceholderLocations(output);

        IReadOnlyList<ReviewFinding> structuredFindings = ReviewResultParser.ParseFindings(sanitized);
        if (structuredFindings.Any(HasStatedDefect))
        {
            return true;
        }

        // Normalized the same way ParseFindings normalizes this exact data (line 47 there): a
        // CRLF-authored pass otherwise leaves a stray '\r' between the two '\n's ParagraphBoundary
        // needs to see a blank line, collapsing every paragraph in the body into one.
        string body = string.Join('\n', sanitized
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !line.TrimStart().StartsWith(VerdictMarker, StringComparison.OrdinalIgnoreCase)));

        return ParagraphBoundary().Split(body)
            .Where(paragraph => LocationPattern().IsMatch(paragraph) && !IsFindingContractExampleEcho(paragraph))
            .Any(paragraph => StructuralMarkerPattern().IsMatch(paragraph) || NamesFindingInProse(paragraph));
    }

    /// <summary>
    /// Whether a paragraph is a structured header carrying a real (non-placeholder) location
    /// glued to the finding contract's own worked-example body, verbatim (adversarial cycle-1
    /// finding, `ReviewVerdictValidation.cs:208`): a resumed session that half-fills the
    /// reprompt's template — a genuine <c>at=</c> tag, but <c>Defect:</c>/<c>Scenario:</c> left
    /// as the literal example text — is exactly the shape <see cref="HasStatedDefect"/> already
    /// rejects for a well-formed structured block (<see cref="IsFindingContractExample"/>), but
    /// only <see cref="StripPlaceholderLocations"/> screens for the placeholder path
    /// (<c>src/Some/File.cs</c>); a real path was never a placeholder, so it survives to the
    /// paragraph scan below untouched. Both of that scan's branches then wrongly read it as a
    /// finding: <see cref="StructuralMarkerPattern"/> because the literal `Defect:`/`Scenario:`
    /// labels are still there, and <see cref="NamesFindingInProse"/> independently, because the
    /// example body's own words ("what is wrong", twice) are defect vocabulary sharing a
    /// sentence with the header's location. Filtering the paragraph out before either branch
    /// runs — rather than trying to patch each one — closes both at once, the same way
    /// <see cref="StripPlaceholderLocations"/> already does for the placeholder-path shape of
    /// this same echo.
    /// </summary>
    private static bool IsFindingContractExampleEcho(string paragraph)
    {
        int defectIndex = paragraph.IndexOf("Defect:", StringComparison.OrdinalIgnoreCase);
        if (defectIndex < 0)
        {
            return false;
        }

        return IsFindingContractExample(paragraph[defectIndex..].Trim());
    }

    /// <summary>
    /// The exact `Defect:`/`Scenario:` lines <c>AgentPromptBuilder.AppendFindingContract</c>
    /// prints as the finding contract's own worked example, normalized one line at a time (a
    /// quoted block keeps the example's leading indentation on every line but the first, which
    /// <see cref="HasStatedDefect"/>'s own <c>Trim()</c> only strips from the outside of the
    /// whole string). Kept in sync with that method by hand, the same way this file's other
    /// doc comments already quote its output verbatim rather than sharing a constant across the
    /// Execution/Review boundary.
    /// </summary>
    private const string FindingContractExampleBody =
        "Defect: one sentence saying what is wrong.\n"
        + "Scenario: the input or state that makes it misbehave, and what goes wrong.";

    /// <summary>
    /// Whether a structured `FINDING:` block states a defect, not just a location (adversarial
    /// cycle-5 finding, `ReviewVerdictValidation.cs:155`): a location on the header tag alone is
    /// half of what a finding requires, and a resumed session that echoes
    /// <c>AppendFindingContract</c>'s own reprompt template ("agents sometimes quote the
    /// instructions before answering", per <see cref="ReviewResultParser"/>'s doc comment)
    /// produces exactly that — a header with a well-formed `at=` tag and nothing else. This
    /// requires the block to carry something past its header line, which is what a real finding's
    /// body looks like whether it is prose, a `Defect:`/`Scenario:` label, or defect vocabulary;
    /// the label and vocabulary checks also cover the (currently unseen) case of a reviewer
    /// packing the defect onto the header line itself.
    /// <para>
    /// A body is not "something past the header line" when that something is the contract's own
    /// worked example, quoted rather than answered (cycle-7 conformance finding,
    /// `ReviewVerdictValidation.cs:202`): the reprompt this class shares with
    /// <c>ReviewEngine.RepromptForVerdictAsync</c> reprints <c>AppendFindingContract</c> in full,
    /// so a session that quotes its own instructions before answering reproduces the example
    /// `Defect:`/`Scenario:` lines verbatim, and a blank-body check alone would read that echo as
    /// a real finding's body.
    /// </para>
    /// </summary>
    private static bool HasStatedDefect(ReviewFinding finding)
    {
        if (finding.Location.IsBlank())
        {
            return false;
        }

        int headerEnd = finding.Text.IndexOf('\n');
        string body = headerEnd < 0 ? string.Empty : finding.Text[(headerEnd + 1)..].Trim();
        if (IsFindingContractExample(body))
        {
            return false;
        }

        return body.IsNotBlank()
            || StructuralMarkerPattern().IsMatch(finding.Text)
            || DefectLanguagePattern().IsMatch(finding.Text);
    }

    /// <summary>
    /// Whether a finding block's body is nothing but the finding contract's own worked example,
    /// each line trimmed before comparing so the example's per-line indentation (preserved by
    /// <see cref="ReviewResultParser.ParseFindings"/>, which only trims the header line) does not
    /// hide a verbatim echo from an exact match.
    /// </summary>
    private static bool IsFindingContractExample(string body) =>
        string.Equals(
            string.Join('\n', body.Split('\n').Select(line => line.Trim())),
            FindingContractExampleBody,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The literal placeholder paths this file's own prompts write into an agent's context:
    /// <c>AppendFindingContract</c>'s header example (<c>src/Some/File.cs:123</c>) and
    /// <c>AppendReviewMechanics</c>' bullet describing how to cite a location
    /// (<c>path/to/file.cs:123</c>). Neither names anywhere in any real repository, so a
    /// location this file's own <see cref="LocationPattern"/> reads as "stated" is read as
    /// nothing of the kind when it is one of these two strings.
    /// </summary>
    private static readonly string[] PlaceholderLocations = ["src/Some/File.cs", "path/to/file.cs"];

    /// <summary>
    /// Whether a location a reviewer's output points at is one of this file's own prompts'
    /// placeholder paths rather than something it actually found: a session that quotes its own
    /// instructions — whether the whole `FINDING:` header-to-`Defect:`/`Scenario:` example, more
    /// of the contract's prose beyond that fixed pair of lines, or a single mechanics bullet in
    /// isolation — reproduces one of these two placeholders verbatim, and no genuine finding is
    /// ever placed at a path this literal and this generic. Checking the placeholder itself,
    /// rather than how much surrounding prompt text came back with it, closes the echo gap
    /// regardless of exactly where the echo stops. Used only by <see cref="StripPlaceholderLocations"/>
    /// now (cycle-9 finding): every other branch reads text that has already had a placeholder
    /// match this same check would have rejected removed from it, so this is the single place
    /// that check still runs.
    /// </summary>
    private static bool IsPlaceholderLocation(string location)
    {
        string path = location.Split(':')[0].Trim().Trim('`');
        return PlaceholderLocations.Any(placeholder => string.Equals(path, placeholder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="text"/> with every <see cref="LocationPattern"/> match that
    /// <see cref="IsPlaceholderLocation"/> recognizes removed, leaving every other match — every
    /// real location a reviewer named — untouched (cycle-9 finding,
    /// `ReviewVerdictValidation.cs:256` and `ReviewVerdictValidation.cs:325`). Run once, before
    /// <see cref="NamesAFinding"/>'s branches see the text, rather than inside each branch: a
    /// per-branch check only screens the branch it is written into, which is how the
    /// structural-marker branch went unscreened while the structured-finding and prose branches
    /// were guarded, and how a placeholder that opened a sentence could swallow a real location
    /// stated later in that same sentence — <see cref="NamesFindingInProse"/> takes only the
    /// first <see cref="LocationPattern"/> match per sentence, so a sentence like "Compare it
    /// against `path/to/file.cs:123`, but `src/Auth.cs:42` never resets the limiter." used to
    /// have its one real location discarded along with the placeholder that preceded it and be
    /// read as naming nothing. With the placeholder gone before either branch runs, the first
    /// match left standing is always a real one.
    /// </summary>
    private static string StripPlaceholderLocations(string text) =>
        LocationPattern().Replace(text, match => IsPlaceholderLocation(match.Value) ? string.Empty : match.Value);

    /// <summary>
    /// The sentence-scoped half of the prose heuristic: a location and defect language in the
    /// same sentence, a location whose very next sentence both continues it (per
    /// <see cref="ContinuationPattern"/>) and carries the defect language itself, or a location
    /// whose own sentence is only a backward pointer (per <see cref="BackwardPointerPattern"/>),
    /// in which case the defect language is looked for in the sentence before it instead.
    /// </summary>
    private static bool NamesFindingInProse(string paragraph)
    {
        string[] sentences = SentenceBoundary().Split(paragraph);
        for (int index = 0; index < sentences.Length; index++)
        {
            if (!LocationPattern().IsMatch(sentences[index]))
            {
                continue;
            }

            if (DefectLanguagePattern().IsMatch(sentences[index]))
            {
                return true;
            }

            string? next = index + 1 < sentences.Length ? sentences[index + 1] : null;
            if (next is not null && ContinuationPattern().IsMatch(next) && DefectLanguagePattern().IsMatch(next))
            {
                return true;
            }

            string? previous = index > 0 ? sentences[index - 1] : null;
            if (previous is not null
                && BackwardPointerPattern().IsMatch(sentences[index])
                && DefectLanguagePattern().IsMatch(previous))
            {
                return true;
            }
        }

        return false;
    }
}
