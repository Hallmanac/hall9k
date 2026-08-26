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
    /// <para>
    /// Cycle-2 review (independent pre-PR pass, adversarial finding): the bare word "no" was
    /// missing even though every neighboring negation ("not", "never", "no longer") was already
    /// in the list, so a plainly stated finding like "There is no test for the archived path"
    /// read as naming nothing.
    /// </para>
    /// <para>
    /// Cycle-3 review (independent pre-PR pass, two conformance findings): the conformance
    /// lens's own "How to review" prose (<c>AgentPromptBuilder.BuildConformanceReview</c>) reports
    /// "unmet" criteria and doctrine a diff "departs" from, and the lens has no structured
    /// findings contract to fall back on — the prose branch below is its only path to naming
    /// anything — yet neither verb, nor the plainly synonymous "violates", "breaks", "lacks" and
    /// "omits" a real reviewer used for the same shapes, was in this list, so a correctly located,
    /// correctly described finding phrased with any of them read as naming nothing.
    /// </para>
    /// <para>
    /// Cycle-4 review (independent pre-PR pass, conformance finding): a guard described as one
    /// that "refuses to finish" after other work has already run was rejected — "refuses" is the
    /// same class of affirmative defect verb "drops" and "overwrites" already cover, just for a
    /// guard that fires too late rather than data that is lost or clobbered.
    /// </para>
    /// <para>
    /// Cycle-5 review (independent pre-PR pass, conformance finding #2): prescriptive doctrine
    /// phrasing ("`RunPaths.cs:23` should be sealed per AGENTS.md") carried no word in this
    /// vocabulary, even though the modal form is how a house-rule departure is most often
    /// written in a repository whose own coding standards are themselves a list of prescriptions
    /// ("seal by default", "every new async method takes a `CancellationToken`"). "delete" was
    /// added alongside it for the same reason a defect can also read as content the diff removes
    /// outright, the same class of gap "drops" and "loses" already cover for content the diff
    /// merely fails to keep.
    /// </para>
    /// <see cref="StructuralMarkerPattern"/> covers the structured contract's own "Defect:" /
    /// "Scenario:" labels, checked at paragraph scope instead of this pattern's sentence scope,
    /// because the two-line `FINDING:` block puts the label on the line after the location.
    /// </summary>
    [GeneratedRegex(
        @"\b(not|no|never|missing|fails?|failing|failed|wrong|incorrect|broken|defect|bug|"
        + @"cannot|can't|won't|doesn't|does not|didn't|no longer|without|unhandled|vulnerable|leaks?|"
        + @"crashes?|throws?|refuses?|silently|drops?|dropped|overwrit(?:ten|es)|duplicat(?:es?|ed)|double-counts?|"
        + @"stale|ignor(?:es|ed)|skips?|skipped|corrupts?|corrupted|loses|lost|mismatch(?:ed)?|inconsistent|"
        + @"deadlocks?|hangs?|stuck|overflows?|unmet|departs?|violat(?:es?|ed)|breaks?|lacks?|omits?|"
        + @"should|delet(?:es?|ed))\b",
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
    /// <para>
    /// "A reader" / "the reader" was added for the same reason (cycle-4 conformance finding): a
    /// reviewer describing what a located comment or doc string fails to convey routinely
    /// continues with "A reader following either pointer lands on unrelated text and never
    /// reaches the rule…" rather than a bare pronoun, and that sentence is exactly as much a
    /// continuation of the location's own sentence as "It is never reset" is — it names no new
    /// location and no new claim of its own, only what the location fails to do for whoever reads
    /// it.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^\s*(it|this|that|these|those|which|(?:a|the) readers?)\b", RegexOptions.IgnoreCase)]
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
    /// Whether a paragraph opens with a markdown heading marker (`#` through `######`) or is
    /// nothing but a bold lead-in on its own line, the two shapes a reviewer uses to title a
    /// finding before describing it in the paragraph that follows (cycle-5 conformance finding
    /// #1, `ReviewVerdictValidation.cs:262`): "### 1. `ReviewEngine.cs:614` — the adversarial
    /// pass is over-screened" states only a location and a label, with the actual defect
    /// ("Stripping the objective … deletes the only location it named") in the next paragraph,
    /// which the paragraph-scoped location-plus-defect check below cannot see on its own —
    /// markdown always puts a heading or a bold lead-in in a paragraph by itself, separated from
    /// its own body by the same blank line <see cref="ParagraphBoundary"/> splits on. Checked
    /// only at the very start of the paragraph, not with <see cref="RegexOptions.Multiline"/>,
    /// so an ordinary sentence paragraph that merely mentions a `#` or `**` partway through —
    /// the shape the platform's own hollow-verdict tests exist to keep rejected — is never
    /// mistaken for a heading.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:#{1,6}[ \t]|\*\*[^\n*]+\*\*[ \t]*(?=\n|$))")]
    private static partial Regex HeadingLikeLeadInPattern();

    /// <summary>
    /// Whether a needs-fixes pass's own output states at least one finding, once the verdict
    /// line itself is set aside: a location the platform can point a human or a fix session at,
    /// paired with defect language close enough to it to plausibly describe what is wrong
    /// there — the same sentence, a sentence that visibly continues it, a sentence before it when
    /// the location's own sentence is only a backward pointer to it, the same paragraph (for the
    /// structured contract's `Defect:`/`Scenario:` labels), or the very next paragraph (when the
    /// location's own paragraph is nothing but a heading or bold lead-in naming it). An output
    /// with nothing left after its verdict line, prose that never points anywhere concrete, or a location mentioned
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
    /// <para>
    /// <paramref name="taskObjective"/> screens a different echo the fixed placeholders above
    /// cannot (adversarial cycle-2 finding, `ReviewVerdictValidation.cs:190`): the conformance
    /// lens's own prompt (<c>AgentPromptBuilder.BuildConformanceReview</c>) prints the task's
    /// objective into the reviewer's context, and an ordinary objective names a real file using
    /// real defect vocabulary ("Stop LogsCommand.cs from resolving a stale run directory"). A
    /// session that restates the objective before concluding satisfies the location-plus-defect
    /// shape below without having found anything, and no fixed placeholder list can catch it,
    /// because the text is arbitrary per-task content rather than this file's own boilerplate.
    /// Stripping a verbatim (case-insensitive) occurrence of the objective before either branch
    /// runs closes the same gap the fixed placeholders close for this file's own prompt text —
    /// narrowly, the same way the rest of this method is a keyword-and-proximity check rather
    /// than a semantic one: a paraphrase or a reflowed quotation of the objective still slips
    /// through, the same class of gap already documented above.
    /// </para>
    /// <para>
    /// <paramref name="taskAcceptanceCriteria"/> screens the identical class of echo for the same
    /// prompt's acceptance-criteria bullets (cycle-3 review, `AgentPromptBuilder.cs:813-817`):
    /// each criterion is arbitrary per-task text exactly like the objective, filed reviewer
    /// findings from this project's own task store show criteria routinely pairing a filename
    /// with a defect word ("LogsCommand.cs resolves a stale run directory"), and a session that
    /// restates one before concluding satisfies the location-plus-defect shape below the same way
    /// restating the objective does. Stripped the same way, one criterion at a time.
    /// </para>
    /// <para>
    /// A paragraph that is itself only a heading or bold lead-in — the numbered `###` title a
    /// reviewer gives a finding before describing it below — borrows defect language from the
    /// very next paragraph rather than requiring both in its own paragraph (cycle-5 conformance
    /// finding #1, `ReviewVerdictValidation.cs:262`): the paragraph-scoped check below could not
    /// see this shape at all, because markdown always separates a heading from its own body with
    /// the same blank line <see cref="ParagraphBoundary"/> splits paragraphs on, so the location
    /// lands in one paragraph and the defect in the next and neither on its own satisfies the
    /// same-paragraph rule. Gated on <see cref="HeadingLikeLeadInPattern"/> rather than applied
    /// to every location-bearing paragraph, the same discipline <see cref="ContinuationPattern"/>
    /// and <see cref="BackwardPointerPattern"/> already apply at sentence scope: an ordinary
    /// affirming paragraph that merely happens to precede a paragraph using defect vocabulary for
    /// something else must not borrow language meant for a different subject.
    /// </para>
    /// </summary>
    public static bool NamesAFinding(
        string? output, string? taskObjective = null, IReadOnlyList<string>? taskAcceptanceCriteria = null)
    {
        if (output.IsBlank())
        {
            return false;
        }

        string sanitized = StripAcceptanceCriteriaEcho(
            StripObjectiveEcho(StripPlaceholderLocations(output), taskObjective), taskAcceptanceCriteria);

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

        return NamesFindingAcrossParagraphs(ParagraphBoundary().Split(body));
    }

    /// <summary>
    /// The paragraph-scoped half of the prose heuristic: a location-bearing paragraph (not the
    /// finding contract's own worked-example echo) that either states a defect itself — a
    /// `Defect:`/`Scenario:` label or <see cref="NamesFindingInProse"/>'s sentence-level check —
    /// or is itself only a heading or bold lead-in immediately followed by a paragraph that
    /// states one (see <see cref="HeadingLikeLeadInPattern"/>).
    /// </summary>
    private static bool NamesFindingAcrossParagraphs(string[] paragraphs)
    {
        for (int index = 0; index < paragraphs.Length; index++)
        {
            string paragraph = paragraphs[index];
            if (!LocationPattern().IsMatch(paragraph) || IsFindingContractExampleEcho(paragraph))
            {
                continue;
            }

            if (StructuralMarkerPattern().IsMatch(paragraph) || NamesFindingInProse(paragraph))
            {
                return true;
            }

            string? next = index + 1 < paragraphs.Length ? paragraphs[index + 1] : null;
            if (next is not null
                && HeadingLikeLeadInPattern().IsMatch(paragraph)
                && DefectLanguagePattern().IsMatch(next))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a paragraph is a structured header carrying a real (non-placeholder) location
    /// glued to the finding contract's own worked-example body (adversarial cycle-1 finding,
    /// `ReviewVerdictValidation.cs:208`; cycle-2 review, conformance finding #1): a resumed
    /// session that half-fills the reprompt's template — a genuine <c>at=</c> tag, but
    /// <c>Defect:</c>/<c>Scenario:</c> left as the literal example text, with or without
    /// something else appended past it — is exactly the shape <see cref="HasStatedDefect"/>
    /// already rejects for a well-formed structured block (<see cref="BeginsWithFindingContractExample"/>),
    /// but only <see cref="StripPlaceholderLocations"/> screens for the placeholder path
    /// (<c>src/Some/File.cs</c>); a real path was never a placeholder, so it survives to the
    /// paragraph scan below untouched. Both of that scan's branches then wrongly read it as a
    /// finding: <see cref="StructuralMarkerPattern"/> because the literal `Defect:`/`Scenario:`
    /// labels are still there, and <see cref="NamesFindingInProse"/> independently, because the
    /// example body's own words ("what is wrong", twice) are defect vocabulary sharing a
    /// sentence with the header's location. Filtering the paragraph out before either branch
    /// runs — rather than trying to patch each one — closes both at once, the same way
    /// <see cref="StripPlaceholderLocations"/> already does for the placeholder-path shape of
    /// this same echo. A prefix match, not an exact one (cycle-2 review): an exact match only
    /// ever caught the two-line example standing completely alone, so a session that quoted
    /// further into the contract's own prose before answering — reprinting the example verbatim
    /// and then continuing to echo the reprompt around it — defeated the exact match while
    /// leaving the example's own words fully intact to trip both branches below anyway.
    /// </summary>
    private static bool IsFindingContractExampleEcho(string paragraph)
    {
        int defectIndex = paragraph.IndexOf("Defect:", StringComparison.OrdinalIgnoreCase);
        if (defectIndex < 0)
        {
            return false;
        }

        return BeginsWithFindingContractExample(paragraph[defectIndex..].Trim());
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
    /// a real finding's body. Nor is it "something past the header line" when the example is
    /// merely how the body OPENS (cycle-2 review, conformance finding #1): a real, non-placeholder
    /// location glued to the example with anything else appended past it — more of the contract's
    /// own reprompt prose, most often — used to defeat the exact-match check below while every
    /// word of the echo it let through was still the contract's own, never the reviewer's.
    /// </para>
    /// <para>
    /// The header's <c>at=</c> tag has to read as a real <see cref="LocationPattern"/> location,
    /// not merely a non-blank one (cycle-4 adversarial finding,
    /// `ReviewVerdictValidation.cs:326`): a half-filled reprompt template like
    /// <c>at=the review loop</c> / <c>Defect: findings are reported above.</c> has a non-blank
    /// header tag and a non-blank body, so the blank check alone let it through as a stated
    /// defect — the exact hollow shape this whole class exists to reject — while the prose branch
    /// below would have rejected the identical text because a bare "the review loop" is not one
    /// of this file's own recognized location shapes.
    /// </para>
    /// </summary>
    private static bool HasStatedDefect(ReviewFinding finding)
    {
        if (finding.Location.IsBlank() || !LocationPattern().IsMatch(finding.Location))
        {
            return false;
        }

        int headerEnd = finding.Text.IndexOf('\n');
        string body = headerEnd < 0 ? string.Empty : finding.Text[(headerEnd + 1)..].Trim();
        if (BeginsWithFindingContractExample(body))
        {
            return false;
        }

        return body.IsNotBlank()
            || StructuralMarkerPattern().IsMatch(finding.Text)
            || DefectLanguagePattern().IsMatch(finding.Text);
    }

    /// <summary>
    /// Whether a finding block's body is nothing but the finding contract's own worked example —
    /// or opens with it and continues into more echoed prose past it (cycle-2 review) — each line
    /// trimmed before comparing so the example's per-line indentation (preserved by
    /// <see cref="ReviewResultParser.ParseFindings"/>, which only trims the header line) does not
    /// hide a verbatim echo from the match. A prefix check rather than an exact-equality one: the
    /// two-line example is peculiar enough phrasing ("one sentence saying what is wrong", "the
    /// input or state that makes it misbehave, and what goes wrong") that no genuine finding ever
    /// reproduces it verbatim, so whatever a session appends after quoting it is never a real
    /// defect description standing on its own — it is either nothing, or more of this same
    /// prompt's own boilerplate.
    /// </summary>
    private static bool BeginsWithFindingContractExample(string body) =>
        string.Join('\n', body.Split('\n').Select(line => line.Trim()))
            .StartsWith(FindingContractExampleBody, StringComparison.OrdinalIgnoreCase);

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
    /// were guarded. <see cref="NamesFindingInProse"/> only ever tests whether a sentence
    /// contains a location at all, not which one, so the risk this step closes for that branch is
    /// narrower than it once was: a sentence naming both a placeholder and a real location, like
    /// "Compare it against `path/to/file.cs:123`, but `src/Auth.cs:42` never resets the limiter.",
    /// is found by the real location either way, but a sentence whose only location is the
    /// placeholder still has to have it removed, or the sentence would be misread as pointing
    /// somewhere.
    /// </summary>
    private static string StripPlaceholderLocations(string text) =>
        LocationPattern().Replace(text, match => IsPlaceholderLocation(match.Value) ? string.Empty : match.Value);

    /// <summary>
    /// <paramref name="text"/> with every verbatim (case-insensitive) occurrence of
    /// <paramref name="taskObjective"/> removed (adversarial cycle-2 finding,
    /// `ReviewVerdictValidation.cs:190`): unlike <see cref="PlaceholderLocations"/>, the objective
    /// is not a fixed string this file can list in advance — it is whatever the task says, printed
    /// into the conformance lens's own prompt by <c>AgentPromptBuilder.BuildConformanceReview</c>.
    /// A session that restates it before concluding reproduces its own file references and defect
    /// vocabulary right back at the platform, so the objective is treated the same way a
    /// placeholder is: read, not found. A no-op when there is no objective to strip, which is
    /// every call outside the conformance lens (the adversarial lens is deliberately never told
    /// the objective, so it has nothing of this shape to echo).
    /// </summary>
    private static string StripObjectiveEcho(string text, string? taskObjective)
    {
        string needle = (taskObjective ?? string.Empty).Trim();
        return needle.IsBlank()
            ? text
            : Regex.Replace(text, Regex.Escape(needle), string.Empty, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// <paramref name="text"/> with every verbatim (case-insensitive) occurrence of each of
    /// <paramref name="taskAcceptanceCriteria"/> removed, the same way <see cref="StripObjectiveEcho"/>
    /// strips the objective (cycle-3 review, `AgentPromptBuilder.cs:813-817`): the criteria bullets
    /// are the identical class of arbitrary per-task text
    /// <c>AgentPromptBuilder.BuildConformanceReview</c> prints into the conformance lens's own
    /// prompt, so a session that restates one before concluding reproduces its own file references
    /// and defect vocabulary right back at the platform the same way restating the objective does.
    /// A no-op when there are no criteria to strip.
    /// </summary>
    private static string StripAcceptanceCriteriaEcho(string text, IReadOnlyList<string>? taskAcceptanceCriteria)
    {
        if (taskAcceptanceCriteria is null || taskAcceptanceCriteria.Count == 0)
        {
            return text;
        }

        string sanitized = text;
        foreach (string criterion in taskAcceptanceCriteria)
        {
            sanitized = StripObjectiveEcho(sanitized, criterion);
        }

        return sanitized;
    }

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
