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
/// The check is deliberately lens-agnostic and format-agnostic: both lenses are handed the same
/// <c>FINDING:</c> structured contract now (<c>AgentPromptBuilder.AppendFindingContract</c>,
/// Decisions Log #87), but a conformance pass still sometimes answers in plain prose rather than
/// the header shape, and that prose pointer reads as a stated location the same way a structured
/// one does, because whether the reviewer used a header is not what this checks — whether it
/// named a place is. A verdict is validated on its raw output, before any parsing decides what to
/// do with what it found.
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
    /// <para>
    /// The extension itself has to be lowercase (cycle-6 finding): a real extension is always
    /// written lowercase in this codebase's own findings (`Auth.cs`, `docs/cli.md`, `cmd.exe`,
    /// `install.ps1`), while a C# `Type.Member` reference an affirming summary is thick with —
    /// `File.WriteAllTextAsync`, `PlatformPaths.Home`, `RunPaths.Root`, `Uri.TryCreate` — reads as
    /// a dotted "name.extension" the same way, because the member name after the dot is at least
    /// two letters same as any real extension is, and this pattern's own `{2,10}` cap just clips
    /// a longer member name down to its first ten letters rather than failing to match it. A
    /// member name is PascalCase by this codebase's own naming convention (see AGENTS.md's coding
    /// standards), so it never starts with a lowercase letter, and requiring the extension segment
    /// itself to start (and stay) lowercase excludes every one of them without excluding any real
    /// extension this codebase's findings actually use — confirmed against the minimal repro
    /// (`` `Uri.TryCreate` handles this correctly; nothing here is wrong. ``) and against a
    /// `File.WriteAllTextAsync`-bearing bullet lifted from one of two recorded needs-fixes lens
    /// outputs from this install's own runs this finding cited (task 4633f355 run 01a03935
    /// review-3 conformance, task 74e572fb run 01a03726 review-9 conformance).
    /// </para>
    /// <para>
    /// Those two recorded outputs, read in full rather than as an isolated bullet, still pass
    /// <see cref="NamesAFinding"/> after this fix, and this fix is not what was ever going to
    /// close that: each also mentions an unrelated real (lowercase-extension) location — `cmd.exe`
    /// in the first, `config.json` in the second — inside a sentence that separately negates its
    /// own claim ("rule 1's ... case never applies", "I did **not** file"), which is the
    /// keyword-and-proximity gap this method's own doc comment already discloses as permanent (an
    /// output whose own sentence negates the defect itself still passes, because "closing that gap
    /// needs reading comprehension a regex cannot do"). A run across this install's own recorded
    /// needs-fixes lens outputs (369 across `~/.hall9k/runs/**` and every project home's task run
    /// directories) confirms this pattern is not unique to these two: of the 369, 333 passed
    /// <see cref="NamesAFinding"/> before this fix and 328 pass after it. Most of the 5 that flip
    /// are, on inspection, genuine findings that happened to pass only because a spurious
    /// `Type.Member` match supplied this method's location gate for a paragraph or sentence whose
    /// own defect language was, coincidentally, either the same pre-existing own-sentence-negation
    /// gap or vocabulary this method's own list does not yet cover. But this narrowing does have a
    /// real cost, confirmed against a sixth recorded output outside that count of 369
    /// (`~/.hall9k/runs/01a031db-ae49-73a3-a8d3-e3117796f0ba/review-2-conformance-findings.md`,
    /// cycle-11 conformance finding): a genuine, cleanly-stated finding whose only location is a backticked
    /// `Type.Member` symbol rather than a file path — "`ProjectHomeRenderEngine.RenderIdea`
    /// creates a decoy `workspace/` directory when …" — has no real (lowercase-extension) location
    /// anywhere in its text, so this narrowing costs `NamesAFinding` its only location gate and
    /// the finding flips from named to unnamed. This narrowing cannot tell that shape apart from
    /// the spurious match it exists to exclude, because both read as a dotted PascalCase name with
    /// no file path in the sentence — trading a known amount of recall (a real finding that names
    /// only a symbol) for the precision cycle-6 fixed (a hollow verdict wrongly credited by an
    /// incidental symbol mention) is what this narrowing does, not a change with no real finding on
    /// either side of it. Recovering that recall needs a distinct location grammar for symbol-only
    /// pointers, not a loosening of the extension-casing rule, which would reopen the cycle-6 gap
    /// this narrowing exists to close. Closing the residual own-sentence-negation and vocabulary
    /// gaps the other flips share is a distinct, pre-existing defect in the paragraph/sentence
    /// proximity heuristic, not something this narrowing was ever positioned to fix.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"[\w/\\-]*[A-Za-z0-9_-]\.[a-z]{2,10}(:\d+)?"
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
    /// <para>
    /// Internal, not private (task: a second fix round over the same findings): <see cref="ReviewFixEscalation"/>
    /// reuses this exact vocabulary for its own human-restatement proximity scan, in the same
    /// assembly, rather than maintaining a second, driftable copy of the same opportunistically-grown list.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(not|no|never|missing|fails?|failing|failed|wrong|incorrect|broken|defect|bug|"
        + @"cannot|can't|won't|doesn't|does not|didn't|no longer|without|unhandled|vulnerable|leaks?|"
        + @"crashes?|throws?|refuses?|silently|drops?|dropped|overwrit(?:ten|es)|duplicat(?:es?|ed)|double-counts?|"
        + @"stale|ignor(?:es|ed)|skips?|skipped|corrupts?|corrupted|loses|lost|mismatch(?:ed)?|inconsistent|"
        + @"deadlocks?|hangs?|stuck|overflows?|unmet|departs?|violat(?:es?|ed)|breaks?|lacks?|omits?|"
        + @"should|delet(?:es?|ed))\b",
        RegexOptions.IgnoreCase)]
    internal static partial Regex DefectLanguagePattern();

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
    /// <para>
    /// One of two alternative "still on the same subject" signals <see cref="StatesDefectWithinLookahead"/>
    /// accepts, the other being <see cref="CodeReferencePattern"/>: this one is a strict grammatical
    /// continuation, that one is a topical one (still quoting the same kind of code artifact the
    /// located sentence did).
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^\s*(it|this|that|these|those|which|(?:a|the) readers?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationPattern();

    /// <summary>
    /// Whether a sentence quotes at least one backtick-delimited code reference — a file, a
    /// symbol, a command — the way this codebase's own recorded reviewer prose almost always does
    /// when it is still describing the located code rather than moving on to something else
    /// (cycle-2 review, conformance finding #1): the two real recorded intervening sentences that
    /// motivated <see cref="DefectLookaheadSentences"/> ("Every failure branch in `Explain`
    /// interpolates `{verb} {key}`…", "the 29 `Decisions Log #77` pointers this branch adds
    /// across `TaskAggregate.cs`, `CloseoutEngine.cs`…") are both dense with these, while the
    /// counter-example that first exposed the risk of dropping <see cref="ContinuationPattern"/>'s
    /// gate entirely — the conformance prompt's own "How to review" bullet, "Judge the work
    /// against the objective … doctrine (AGENTS.md or CLAUDE.md …). Report criteria the diff
    /// leaves unmet …" — has none: its second sentence is a fresh instruction to the reviewer, not
    /// a continued description of `AGENTS.md`/`CLAUDE.md`, and quoting neither backtick nor a
    /// continuation pronoun is exactly what tells the two apart.
    /// </summary>
    [GeneratedRegex(@"`[^`\n]+`")]
    private static partial Regex CodeReferencePattern();

    /// <summary>
    /// A markdown list marker starting a new line inside what <see cref="SentenceBoundary"/> read
    /// as a single sentence (found verifying the fix above against this file's own conformance
    /// prompt, `AgentPromptBuilder.BuildConformanceReview`): that splitter has no notion of a
    /// bullet list, so a bullet with no terminal-punctuation-plus-capital-letter break — "…any
    /// house rule it departs from.\n- You are in the implementation's git worktree on branch
    /// `task/1-slug`." — reads as one sentence spanning two unrelated list items. Without this
    /// guard, the second item's own backtick-quoted branch name satisfied
    /// <see cref="CodeReferencePattern"/> for the whole merged "sentence", so the platform's own
    /// prompt text — "doctrine (AGENTS.md or CLAUDE.md …). Report criteria the diff leaves
    /// unmet…" glued to the next bullet's `task/1-slug` — read as a stated finding against
    /// AGENTS.md. A candidate that contains this shape cannot be trusted for either signal, code
    /// reference or defect vocabulary alike, because there is no way to tell which list item
    /// either one actually belongs to.
    /// </summary>
    [GeneratedRegex(@"\n[ \t]*[-*][ \t]")]
    private static partial Regex EmbeddedListMarkerPattern();

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
    /// <para>
    /// The backward-pointer branch in <see cref="NamesFindingInProse"/> also screens the
    /// sentence it borrows from against <see cref="HeadingDenialPattern"/> now (cycle-11
    /// adversarial finding,
    /// `ReviewVerdictValidation.cs:810`): every other branch that borrows defect language from a
    /// neighbouring sentence or paragraph — <see cref="StatesDefectWithinLookahead"/>,
    /// <see cref="StatesDefectInLaterParagraph"/>, the lead-in gate in
    /// <see cref="NamesFindingAcrossParagraphs"/> — already stops on the denial idiom, but this
    /// branch looks backward instead of forward and had no equivalent stop. A reviewer writing "I
    /// checked every branch and no defect stands. See ReviewEngine.cs:612." puts the denial idiom
    /// in the sentence before a bare pointer, and without this screen "no" and "defect" — there to
    /// deny a problem, not assert one — supplied the pointer's location with defect language it
    /// never earned.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^\s*(see|this is at|it is in)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackwardPointerPattern();

    /// <summary>
    /// Whether a paragraph is nothing but a markdown heading marker (`#` through `######`) or a
    /// bold lead-in on its own line, the two shapes a reviewer uses to title a finding before
    /// describing it in the paragraph that follows (cycle-5 conformance finding #1,
    /// `ReviewVerdictValidation.cs:262`): "### 1. `ReviewEngine.cs:614` — the adversarial pass is
    /// over-screened" states only a location and a label, with the actual defect ("Stripping the
    /// objective … deletes the only location it named") in the next paragraph, which the
    /// paragraph-scoped location-plus-defect check below cannot see on its own.
    /// <para>
    /// Anchored to the end of the paragraph, not just the end of the heading's own line (cycle-10
    /// conformance finding #2, `ReviewVerdictValidation.cs:231`): the doc comment's premise —
    /// "markdown always puts a heading … in a paragraph by itself" — only holds when a blank line
    /// actually separates the heading from its body. A reviewer who titles a finding with
    /// "## Conformance review of `ReviewEngine.cs`" and continues describing something unrelated
    /// on the very next line, with no blank line between them, keeps both lines in one
    /// <see cref="ParagraphBoundary"/>-delimited paragraph, and the old `#` alternative — which
    /// only required the marker and a space, with nothing checking what followed — read that
    /// whole two-line paragraph as a bare lead-in anyway, letting the heading branch below borrow
    /// defect language from body text that belongs to an entirely different paragraph's worth of
    /// prose. The bold alternative's existing <c>(?=\n|$)</c> has the identical gap for the same
    /// reason (it only ever checks the position right after the closing <c>**</c>, not whether
    /// anything follows later in the paragraph), so both alternatives now require the marker to
    /// consume the rest of the paragraph, not just the rest of its own line.
    /// </para>
    /// <para>
    /// The bold alternative tolerates one embedded, single-asterisk italic run, and — like the
    /// `#` alternative always has — a trailing label on the same line past the closing marker
    /// (cycle-2 review, two findings): a reviewer's own emphasis inside a bold lead-in ("the
    /// live-run count measured *before* its own claims") broke the old
    /// <c>\*\*[^\n*]+\*\*</c>, which read any inner <c>*</c> as the closing marker and then found
    /// no matching close for the rest of the line, so the paragraph matched neither alternative at
    /// all. And a bold location immediately followed by a short dash-led label on the very same
    /// line ("**`CardPublicationEngine.cs:274`** — the adoption path records the caution twice.")
    /// isn't the finding contract's own worked-example shape either, but the old bold alternative's
    /// <c>$</c> anchor — added for the cycle-10 gap above — refused it the identical way it refused
    /// a genuine multi-sentence heading-plus-body paragraph, because both anchor at the very end of
    /// the paragraph and neither distinguished "one more short label" from "an entire second
    /// paragraph's worth of unrelated prose glued on." What actually tells them apart is a line
    /// break: the cycle-10 shape only ever reads as one paragraph because its body starts on a new
    /// line with no blank line before it, while a same-line label never contains one, so requiring
    /// the whole match to end at <c>$</c> without ever crossing a <c>\n</c> (<c>[^\n]</c> never
    /// matches one, and <c>$</c> without <see cref="RegexOptions.Multiline"/> only matches true
    /// end-of-string) keeps the cycle-10 paragraph excluded while finally letting a same-line
    /// label through.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^\s*(?:#{1,6}[ \t][^\n]*|\*\*(?:[^\n*]|\*(?!\*))+\*\*[ \t]*[^\n]*)$")]
    private static partial Regex HeadingLikeLeadInPattern();

    /// <summary>
    /// The specific denial idiom a reviewer uses to say a heading's location has nothing wrong
    /// with it — "Nothing is wrong", "no defect(s) stand" — rather than to name a defect (cycle-10
    /// adversarial finding #2, `ReviewVerdictValidation.cs:371`): the sentence-scoped branches
    /// guard against an unrelated affirming sentence borrowing defect vocabulary by restricting
    /// the forward search in <see cref="NamesFindingInProse"/> to a bounded lookahead that stops
    /// at the first sentence this pattern recognizes as a denial, which is exactly why "Nothing
    /// here is wrong." can never supply defect language for a preceding sentence there. The
    /// heading-lead-in branch below had no equivalent restriction: it accepted any
    /// <see cref="DefectLanguagePattern"/> match anywhere in a later paragraph, so a reviewer who
    /// titles an empty verdict with a filename ("## Findings for `ReviewEngine.cs`") and concludes
    /// "Nothing is wrong; no defect stands." tripped the heading branch purely because "wrong",
    /// "no" and "defect" are all defect vocabulary, even though every one of them is being used to
    /// deny a problem rather than assert one. Deliberately narrow and literal, the same discipline
    /// the rest of this file's opportunistic vocabulary follows: this does not attempt the general
    /// "is this defect language negated" question <see cref="NamesAFinding"/>'s own doc comment
    /// already discloses as a permanent, out-of-scope gap for defect language sharing a sentence
    /// with its location — it screens each branch that borrows defect language from text other
    /// than the location's own sentence against these two concrete denial shapes a reviewer
    /// plausibly writes to close out a hollow needs-fixes: the heading branch's much weaker signal
    /// (defect vocabulary anywhere in an unrelated later paragraph), <see cref="StatesDefectWithinLookahead"/>'s
    /// forward lookahead, and <see cref="NamesFindingInProse"/>'s backward-pointer branch. Also
    /// screened against the lead-in paragraph's own text now that <see cref="HeadingLikeLeadInPattern"/>
    /// can match a bold lead-in with a same-line trailing label (cycle-2 review): a label that
    /// already denies its own location ("**`Foo.cs:10`** — nothing wrong here.") must not be read
    /// as a heading needing to borrow from whatever unrelated defect language happens to sit in
    /// the next paragraph.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:nothing|none)\b[^.!?]{0,40}\b(?:wrong|broken|amiss|defects?|bugs?|issues?|problems?)\b"
        + @"|\bno\b[^.!?]{0,10}\b(?:defects?|bugs?|issues?|problems?)\s+(?:stands?|remains?|exists?|found)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex HeadingDenialPattern();

    /// <summary>
    /// Whether a needs-fixes pass's own output states at least one finding, once the verdict
    /// line itself is set aside: a location the platform can point a human or a fix session at,
    /// paired with defect language close enough to it to plausibly describe what is wrong
    /// there — the same sentence, a sentence within a short lookahead of it, a sentence before it
    /// when the location's own sentence is only a backward pointer to it, the same paragraph (for
    /// the structured contract's `Defect:`/`Scenario:` labels), or a later paragraph (when the
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
    /// <paramref name="priorRulingReasons"/> screens the same class of echo for a human's own
    /// <c>h9k review resolve --reason</c> text, printed into both lenses' prompts by
    /// <c>AgentPromptBuilder.AppendSettledRulings</c> (task: review prompts carry prior rulings):
    /// that text is exactly as arbitrary as the objective or a criterion, and this codebase's own
    /// recorded review-park reasons routinely pair a real location with real defect vocabulary
    /// ("`config.json` is not reset across restarts") for the same reason a criterion does.
    /// Stripped the same way, one ruling's printed reason at a time — the exact text the prompt
    /// shows, already truncated and reduced to one line by <c>AgentPromptBuilder.RulingReasonsShown</c>,
    /// not the human's full untruncated reason, so the strip matches what a reviewer could
    /// actually have echoed.
    /// </para>
    /// <para>
    /// A paragraph that is itself only a heading or bold lead-in — the numbered `###` title a
    /// reviewer gives a finding before describing it below — borrows defect language from a later
    /// paragraph rather than requiring both in its own paragraph (cycle-5 conformance finding #1,
    /// `ReviewVerdictValidation.cs:262`; extended to more than one paragraph ahead, cycle-2
    /// review): the paragraph-scoped check below could not see this shape at all, because markdown
    /// always separates a heading from its own body with the same blank line
    /// <see cref="ParagraphBoundary"/> splits paragraphs on, so the location lands in one paragraph
    /// and the defect in a later one and neither on its own satisfies the same-paragraph rule.
    /// Gated on <see cref="HeadingLikeLeadInPattern"/> rather than applied to every
    /// location-bearing paragraph, the same discipline <see cref="BackwardPointerPattern"/> already
    /// applies at sentence scope: an ordinary affirming paragraph that merely happens to precede a
    /// paragraph using defect vocabulary for something else must not borrow language meant for a
    /// different subject.
    /// </para>
    /// </summary>
    public static bool NamesAFinding(
        string? output, string? taskObjective = null, IReadOnlyList<string>? taskAcceptanceCriteria = null,
        IReadOnlyList<string>? priorRulingReasons = null)
    {
        if (output.IsBlank())
        {
            return false;
        }

        string sanitized = StripVerbatimEchoes(
            StripVerbatimEchoes(
                StripObjectiveEcho(StripPlaceholderLocations(output), taskObjective), taskAcceptanceCriteria),
            priorRulingReasons);

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
    /// or is itself only a heading or bold lead-in, in which case the defect is looked for in the
    /// paragraphs that follow it (see <see cref="HeadingLikeLeadInPattern"/>), each one screened
    /// the same two ways the header-to-body shape already is: it must not be the finding
    /// contract's own worked example quoted rather than answered (cycle-10 conformance finding
    /// #1, `ReviewVerdictValidation.cs:368` — the heading-only paragraph borrowed the example
    /// body's own "what is wrong" the same way an unscreened structured header once did), and it
    /// must not be a bare denial of the kind <see cref="HeadingDenialPattern"/> recognizes
    /// (cycle-10 adversarial finding #2).
    /// <para>
    /// The search past the lead-in is not limited to the single paragraph right after it
    /// (cycle-2 review, two findings): the dominant shape in this codebase's own recorded
    /// conformance output is lead-in, then a neutral paragraph describing the mechanism with no
    /// vocabulary word of its own, then a `Failure scenario:` paragraph that actually states what
    /// goes wrong — three paragraphs, not two — and a check that only ever looked at
    /// <c>paragraphs[index + 1]</c> could never reach the third. The scan instead walks forward
    /// until it finds one it can use, or hits a reason to stop: another lead-in (a different
    /// finding starting; its own defect language belongs to it, not this one), a paragraph the
    /// finding contract's own example would produce (screened the same as before), or a denial.
    /// A later paragraph that itself carries a real <see cref="LocationPattern"/> match also ends
    /// the search — a fresh location is the surest sign this lead-in's own text has run out and a
    /// different point is being made — but only a real one: the finding-contract example is
    /// screened first so its own placeholder path can never be read as that fresh location. The
    /// lead-in paragraph's own text is screened against <see cref="HeadingDenialPattern"/> too,
    /// now that <see cref="HeadingLikeLeadInPattern"/> can match a same-line trailing label: a
    /// lead-in that already denies its own location in that label must not go looking for defect
    /// language belonging to something else entirely.
    /// </para>
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

            if (HeadingLikeLeadInPattern().IsMatch(paragraph)
                && !HeadingDenialPattern().IsMatch(paragraph)
                && StatesDefectInLaterParagraph(paragraphs, index))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The forward walk <see cref="NamesFindingAcrossParagraphs"/>'s heading branch runs past a
    /// lead-in, looking for the paragraph that actually states the defect it named.
    /// </summary>
    private static bool StatesDefectInLaterParagraph(string[] paragraphs, int leadInIndex)
    {
        for (int ahead = leadInIndex + 1; ahead < paragraphs.Length; ahead++)
        {
            string candidate = paragraphs[ahead];
            if (IsFindingContractExampleEcho(candidate))
            {
                continue;
            }

            if (HeadingLikeLeadInPattern().IsMatch(candidate) || LocationPattern().IsMatch(candidate))
            {
                return false;
            }

            if (HeadingDenialPattern().IsMatch(candidate))
            {
                return false;
            }

            if (StructuralMarkerPattern().IsMatch(candidate) || DefectLanguagePattern().IsMatch(candidate))
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
    /// <see cref="ReviewResultParser.ExampleLocationPlaceholder"/>'s path half, the same
    /// worked example <c>AppendFindingContract</c> interpolates it into, and
    /// <c>AppendReviewMechanics</c>' own bullet describing how to cite a location
    /// (<c>path/to/file.cs:123</c>). Neither names anywhere in any real repository, so a
    /// location this file's own <see cref="LocationPattern"/> reads as "stated" is read as
    /// nothing of the kind when it is one of these two strings. The first is read off
    /// <see cref="ReviewResultParser.ExampleLocationPlaceholder"/> rather than copied as its own
    /// literal, so the two can never drift apart the way they did before this line existed.
    /// </summary>
    private static readonly string[] PlaceholderLocations =
        [ReviewResultParser.ExampleLocationPlaceholder.Split(':')[0], "path/to/file.cs"];

    /// <summary>
    /// Whether a location a reviewer's output points at is one of this file's own prompts'
    /// placeholder paths rather than something it actually found: a session that quotes its own
    /// instructions — whether the whole `FINDING:` header-to-`Defect:`/`Scenario:` example, more
    /// of the contract's prose beyond that fixed pair of lines, or a single mechanics bullet in
    /// isolation — reproduces one of these two placeholders verbatim, and no genuine finding is
    /// ever placed at a path this literal and this generic. Checking the placeholder itself,
    /// rather than how much surrounding prompt text came back with it, closes the echo gap
    /// regardless of exactly where the echo stops. Used by <see cref="StripPlaceholderLocations"/>
    /// (cycle-9 finding): every other branch in this file reads text that has already had a
    /// placeholder match this same check would have rejected removed from it, so this is the
    /// single place that check still runs here. Also internal so
    /// <see cref="ReviewResultParser.Close"/> can screen a structured finding's own `at=` tag the
    /// identical path-first way, rather than the narrower exact-literal match it used to use
    /// (cycle-3 adversarial finding): a line number dropped or adapted from
    /// <see cref="ReviewResultParser.ExampleLocationPlaceholder"/> defeated that exact match while
    /// still pointing at a path no repository has.
    /// </summary>
    internal static bool IsPlaceholderLocation(string location)
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
    /// <paramref name="taskObjective"/> reduced to whatever <see cref="LocationPattern"/> matches
    /// it contains, everything else in that span removed (adversarial cycle-2 finding,
    /// `ReviewVerdictValidation.cs:190`): unlike <see cref="PlaceholderLocations"/>, the objective
    /// is not a fixed string this file can list in advance — it is whatever the task says, printed
    /// into the conformance lens's own prompt by <c>AgentPromptBuilder.BuildConformanceReview</c>.
    /// A session that restates it before concluding reproduces its own file references and defect
    /// vocabulary right back at the platform, so the objective is treated the same way a
    /// placeholder is: read, not found. A no-op when there is no objective to strip, which is
    /// every call outside the conformance lens (the adversarial lens is deliberately never told
    /// the objective, so it has nothing of this shape to echo).
    /// <para>
    /// Blanking the whole matched span used to delete the location along with everything else
    /// (adversarial cycle-10 finding, `ReviewVerdictValidation.cs:326`): this codebase's own
    /// acceptance criteria routinely pair a filename with a defect word — the doc comment below
    /// cites exactly that shape — so a conformance session that restates a criterion as part of a
    /// genuine finding ("`LogsCommand.cs` resolves an archived task's run directory — UNMET.")
    /// lost its only stated location the instant the criterion text was blanked, even though the
    /// reviewer's own added words ("UNMET", or whatever came after) were real defect language the
    /// stripping was never meant to touch. Keeping the location and discarding only the rest of
    /// the echoed span closes the same restated-objective/criterion hole (no defect vocabulary
    /// from the objective itself survives to falsely pair with a location the reviewer never
    /// actually analyzed) without also erasing a location a real finding legitimately shares with
    /// the task's own wording.
    /// </para>
    /// <para>
    /// Below <see cref="MinEchoNeedleLength"/>, the strip is skipped entirely rather than matched
    /// on word boundaries (cycle-6 human triage, task: review prompts carry prior rulings): a
    /// human's own <c>h9k review resolve --reason</c> text can be as short as "no", which is also
    /// <see cref="DefectLanguagePattern"/>'s own bare negation word, so even a boundary-respecting
    /// match would still blank a genuine reviewer's "has no timeout guard" the same way it blanks
    /// the echoed reason — there is no way to tell the two apart from the needle alone. An
    /// objective or acceptance criterion this short has never been observed in practice, so the
    /// guard costs nothing there. Above the minimum, the match is bounded with <c>\b</c> on
    /// whichever edge is itself a word character, so a needle like "no" cannot mangle the middle
    /// of an unrelated word ("Notify", "cannot") the way an unbounded substring match would.
    /// </para>
    /// </summary>
    private static string StripObjectiveEcho(string text, string? taskObjective)
    {
        string needle = (taskObjective ?? string.Empty).Trim();
        if (needle.Length < MinEchoNeedleLength)
        {
            return text;
        }

        string pattern =
            (char.IsLetterOrDigit(needle[0]) ? @"\b" : string.Empty) + Regex.Escape(needle)
            + (char.IsLetterOrDigit(needle[^1]) ? @"\b" : string.Empty);
        return Regex.Replace(
            text,
            pattern,
            match => string.Join(' ', LocationPattern().Matches(match.Value).Select(m => m.Value)),
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Below this many characters, <see cref="StripObjectiveEcho"/> skips the strip rather than
    /// risk it — see that method's own doc comment for why.
    /// </summary>
    private const int MinEchoNeedleLength = 4;

    /// <summary>
    /// <paramref name="text"/> with every verbatim (case-insensitive) occurrence of each of
    /// <paramref name="snippets"/> removed, the same way <see cref="StripObjectiveEcho"/> strips
    /// the objective (cycle-3 review, `AgentPromptBuilder.cs:813-817`; extended to prior-ruling
    /// reasons, task: review prompts carry prior rulings): an acceptance-criteria bullet and a
    /// human's own review-park <c>--reason</c> text are the identical class of arbitrary per-task
    /// text this file's own prompts print — <c>AgentPromptBuilder.BuildConformanceReview</c> for
    /// the former, <c>AgentPromptBuilder.AppendSettledRulings</c> for the latter — so a session
    /// that restates one before concluding reproduces its own file references and defect
    /// vocabulary right back at the platform the same way restating the objective does. Generic
    /// over which per-task text is being screened rather than named for one caller, because both
    /// need the identical strip. A no-op when there is nothing to strip.
    /// </summary>
    private static string StripVerbatimEchoes(string text, IReadOnlyList<string>? snippets)
    {
        if (snippets is null || snippets.Count == 0)
        {
            return text;
        }

        string sanitized = text;
        foreach (string snippet in snippets)
        {
            sanitized = StripObjectiveEcho(sanitized, snippet);
        }

        return sanitized;
    }

    /// <summary>
    /// How far past a location's own sentence <see cref="NamesFindingInProse"/> looks for defect
    /// language that belongs to it (cycle-2 review, conformance finding #1): two of this
    /// codebase's own recorded conformance findings state exactly what the located code does in
    /// the sentence right after the location — "Every failure branch in `Explain` interpolates
    /// `{verb} {key}`…" — and only say what is wrong with it a second sentence later ("the subject
    /// is **wrong** in the first command…"), so a lookahead of one sentence, even a pronoun-gated
    /// one, could not reach it. Bounded rather than run to the end of the paragraph: an
    /// unrelated affirming sentence sharing the paragraph with a genuine defect elsewhere in it is
    /// exactly the shape this whole heuristic exists to avoid over-crediting, and the two filed
    /// findings this bound was written to close both land within it.
    /// </summary>
    private const int DefectLookaheadSentences = 2;

    /// <summary>
    /// The sentence-scoped half of the prose heuristic: a location and defect language in the
    /// same sentence, defect language within <see cref="DefectLookaheadSentences"/> sentences
    /// after it (see <see cref="StatesDefectWithinLookahead"/>), or a location whose own sentence
    /// is only a backward pointer (per <see cref="BackwardPointerPattern"/>), in which case the
    /// defect language is looked for in the sentence before it instead.
    /// <para>
    /// The same-sentence check is guarded by <see cref="EmbeddedListMarkerPattern"/> too
    /// (independent pre-PR review, cycle 2, adversarial finding), not only the lookahead
    /// candidates <see cref="StatesDefectWithinLookahead"/> already guards: <see cref="SentenceBoundary"/>
    /// cannot split at a bare markdown bullet, so two adjacent list items — a location in one, a
    /// coincidentally-worded closing remark in the next — arrive here as a single merged
    /// "sentence" and, without this guard, credited each other directly rather than through the
    /// lookahead this pattern already screens. The same doc comment on <see cref="EmbeddedListMarkerPattern"/>
    /// already states the rule: a candidate containing this shape cannot be trusted for either
    /// signal, because there is no way to tell which list item either one actually belongs to.
    /// </para>
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

            if ((DefectLanguagePattern().IsMatch(sentences[index])
                    && !EmbeddedListMarkerPattern().IsMatch(sentences[index]))
                || StatesDefectWithinLookahead(sentences, index))
            {
                return true;
            }

            string? previous = index > 0 ? sentences[index - 1] : null;
            if (previous is not null
                && BackwardPointerPattern().IsMatch(sentences[index])
                && DefectLanguagePattern().IsMatch(previous)
                && !HeadingDenialPattern().IsMatch(previous))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a sentence within <see cref="DefectLookaheadSentences"/> sentences after
    /// <paramref name="sentences"/>[<paramref name="locationIndex"/>] states the defect (cycle-2
    /// review, conformance finding #1): neither of the two filed findings' intervening sentences
    /// ("Every failure branch in `Explain` interpolates…", "the 29 `Decisions Log #77` pointers
    /// this branch adds across `TaskAggregate.cs`…") opens with a <see cref="ContinuationPattern"/>
    /// pronoun, so the walk cannot be gated on that alone the way the single-sentence check used to
    /// be. It is gated instead on either that pronoun OR <see cref="CodeReferencePattern"/> — the
    /// walk stops the moment a sentence has neither, rather than reading through it hoping a later
    /// one reconnects, because a sentence with neither signal is exactly what let this same
    /// broadening regress a passing case caught verifying the fix: the conformance prompt's own
    /// "How to review" bullet locates `AGENTS.md`/`CLAUDE.md` in one sentence and, with no gate at
    /// all, would have credited the next one ("Report criteria the diff leaves unmet…") purely
    /// because "unmet" is defect vocabulary. <see cref="EmbeddedListMarkerPattern"/> guards a
    /// second way that same regression surfaced: that "unmet" sentence has no pronoun of its own,
    /// but <see cref="SentenceBoundary"/> does not recognize a markdown bullet as a sentence break,
    /// so it reads on into the NEXT bullet ("- You are in the implementation's git worktree on
    /// branch `task/1-slug`.") as the same sentence, and that bullet's own backtick would satisfy
    /// <see cref="CodeReferencePattern"/> for content having nothing to do with it. <see cref="HeadingDenialPattern"/>
    /// still stops the walk on the documented affirming-review idiom ("Every criterion is met, and
    /// Program.cs proves it. Nothing here is wrong.") even on a sentence that would otherwise pass
    /// the topic gate. This narrows, rather than closes, the same keyword-and-proximity gap
    /// <see cref="NamesAFinding"/>'s own doc comment already discloses — an on-topic intervening
    /// sentence that uses defect vocabulary about something other than the located subject can
    /// still supply a false credit, the same class of gap the rest of this file's vocabulary
    /// already accepts.
    /// </summary>
    private static bool StatesDefectWithinLookahead(string[] sentences, int locationIndex)
    {
        int limit = Math.Min(sentences.Length, locationIndex + 1 + DefectLookaheadSentences);
        for (int ahead = locationIndex + 1; ahead < limit; ahead++)
        {
            string candidate = sentences[ahead];
            if (EmbeddedListMarkerPattern().IsMatch(candidate)
                || (!ContinuationPattern().IsMatch(candidate) && !CodeReferencePattern().IsMatch(candidate)))
            {
                return false;
            }

            if (HeadingDenialPattern().IsMatch(candidate))
            {
                return false;
            }

            if (DefectLanguagePattern().IsMatch(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
