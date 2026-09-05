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
    [GeneratedRegex(LocationShape)]
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
    /// <para>
    /// The "should" entry cycle-5 added for prescriptive doctrine phrasing ("`RunPaths.cs:23`
    /// should be sealed") also matches the affirming, status-quo sense of "should" (independent
    /// pre-PR review, conformance finding, task 29025f60): "the naming should stay as it is" names
    /// no defect at all, it affirms that nothing needs to change, but the bare word still credited
    /// as defect language wherever it sat outside a denial's own matched span — including a
    /// clause preceding an unrelated trailing denial, the exact shape <see cref="StatesDefectOutsideDenial"/>
    /// exists to credit for a real defect. "should" followed immediately by "stay"/"remain"/"continue"
    /// and then "as it is"/"as they are" is excluded as this one recorded affirming shape.
    /// </para>
    /// <para>
    /// That exclusion was itself too wide (independent pre-PR review, both lenses, cycle 4, task
    /// 29025f60): "should stay"/"should remain"/"should continue" are not exclusively the
    /// status-quo idiom above — "the lease should remain held until the run ends; it is released
    /// at the first heartbeat gap." and "the walk should continue past a project it cannot read"
    /// are both prescriptive, naming what the code should do that it does not, yet the old
    /// unconditional exclusion stripped "should" from both regardless. The exclusion now also
    /// requires the trailing "as it is"/"as they are" the cycle-5 idiom is actually phrased with,
    /// so it excludes that one recorded affirming shape without swallowing a "should
    /// remain"/"should continue" that goes on to prescribe something else entirely.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(not|no|never|missing|fails?|failing|failed|wrong|incorrect|broken|defect|bug|"
        + @"cannot|can't|won't|doesn't|does not|didn't|no longer|without|unhandled|vulnerable|leaks?|"
        + @"crashes?|throws?|refuses?|silently|drops?|dropped|overwrit(?:ten|es)|duplicat(?:es?|ed)|double-counts?|"
        + @"stale|ignor(?:es|ed)|skips?|skipped|corrupts?|corrupted|loses|lost|mismatch(?:ed)?|inconsistent|"
        + @"deadlocks?|hangs?|stuck|overflows?|unmet|departs?|violat(?:es?|ed)|breaks?|lacks?|omits?|"
        + @"should(?!\s+(?:stay|remain|continue)\s+as\s+(?:it|they)\s+(?:is|are)\b)|delet(?:es?|ed))\b",
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
    /// <para>
    /// Captured in its own group (cycle-1 pre-PR review on the bullet-first split added to
    /// <see cref="NamesFindingInProse"/>) so <see cref="Regex.Split(string)"/> preserves each
    /// marker as its own element of the returned array, rather than consuming it: that is what
    /// lets <see cref="StatesDefectWithinLookahead"/> still see the boundary between two bullets
    /// after they have been split apart, and stop its walk there instead of reading across it.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"(\n[ \t]*[-*][ \t])")]
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
    /// already denies its own location ("**`Foo.cs:10`** — nothing here is wrong.") must not be read
    /// as a heading needing to borrow from whatever unrelated defect language happens to sit in
    /// the next paragraph. <see cref="NamesFindingInProse"/>'s same-sentence check screens against
    /// this pattern too (cycle-3 review, both lenses): a bullet item that denies a defect at its
    /// own location ("`Foo.cs:10` — the catch order is correct; nothing is wrong here.") shares its
    /// sentence with the location rather than borrowing from another one, so this is the one branch
    /// where the location's own sentence supplies both the "nothing/none" and the vocabulary word,
    /// not text external to it — which is exactly why <c>nothing</c>/<c>none</c> must be read as
    /// the sentence's own grammatical subject (followed, within a few words, by a copula —
    /// "is"/"are"/"was"/"were" — before the vocabulary word) rather than merely co-occurring with
    /// it anywhere in the same sentence: "does nothing about the problem" or "logs nothing about
    /// the defect" use "nothing" as a verb's object to describe a real omission, not as the
    /// sentence's subject denying one, and "there is nothing wrong with the naming, but `Auth.cs:42`
    /// never disposes the stream" has its copula in front of "nothing", denying a problem with the
    /// naming rather than with the location that follows. None of these should read as a denial of
    /// the location's own defect, and requiring the subject-copula shape is what keeps them out
    /// without narrowing the two idioms above, which already state their copula explicitly
    /// ("Nothing is wrong", "no defect(s) stand"). The bridging verb need not be a literal copula:
    /// "Nothing about it stands as a defect." denies the same way "is wrong" does, so
    /// <c>stands</c>/<c>remains</c>/<c>exists</c> count alongside <c>is</c>/<c>are</c>/<c>was</c>/
    /// <c>were</c> — the same verbs the "no defect(s) stand(s)" idiom already uses, just with
    /// "nothing"/"none" as the subject instead of the defect noun.
    /// <para>
    /// The subject-copula requirement above closed the two object-of-verb false positives it
    /// names, but it overshot (cycle-6 verify finding): it also stopped recognizing the most
    /// canonical denial shape of all, where the vocabulary word directly post-modifies
    /// "nothing"/"none" with no verb between them at all — "found nothing wrong", "nothing wrong
    /// here", "found nothing I could verify as broken" (the last one drawn verbatim from a
    /// recorded adversarial pass on this install). Because all three sentence-scoped branches
    /// share this one pattern, that regression reopened the denial screen everywhere, not just on
    /// the heading branch it was written for. A second alternative restores that coverage, but a
    /// bare "nothing/none … wrong" cannot by itself tell that shape apart from "there is nothing
    /// wrong with the naming, but `Auth.cs:42` never disposes the stream" — a denial of one thing
    /// that leaves a different, real defect standing — since both are direct post-modification.
    /// What distinguishes them is what precedes "nothing": the false-positive shape always has an
    /// existential copula immediately in front of it ("there IS nothing wrong"), where the
    /// genuine denial does not ("I found nothing wrong", "nothing wrong here"). The second
    /// alternative excludes exactly that one preceding shape rather than solving the general
    /// contrastive-clause question, the same literal, narrow discipline the rest of this pattern
    /// already follows — it does not, for instance, exclude "nothing wrong" preceded by some
    /// other copula-free contrastive clause, which is left as the same kind of accepted recall
    /// gap <see cref="NamesAFinding"/>'s own doc comment already discloses elsewhere in this file.
    /// </para>
    /// <para>
    /// The exclusion above overshot in the other direction (cycle-8 conformance and adversarial
    /// findings, both independently reproducing the same regression): keying the exclusion off
    /// whatever precedes "nothing" ("there IS nothing wrong") throws out the single most common
    /// denial of all, "There is nothing wrong here.", along with every other existential-"there"
    /// denial that never continues into a contrastive clause at all. What actually distinguishes
    /// the false-positive shape this exclusion exists for ("there is nothing wrong with the
    /// naming, but `Auth.cs:42` never disposes the stream") from a plain denial is not what comes
    /// before "nothing", it is the "but"-led contrastive clause that follows the adjective and
    /// names a different, real defect — so the exclusion now looks forward for that clause
    /// instead of backward for the copula, bounded the same 20-30 characters the rest of this
    /// pattern already bounds its proximity checks to.
    /// </para>
    /// <para>
    /// A verbless denial can also link "nothing"/"none" to its vocabulary word with something
    /// other than a bare copula (cycle-8 conformance and adversarial findings): "I found nothing
    /// that qualifies as a defect", "Nothing here amounts to a defect" and "turned up nothing
    /// worth calling a bug" all deny the same way "nothing is wrong" does, just with a bridging
    /// verb phrase instead of a one-word copula, so the first alternative's bridging-verb list
    /// grows to include them rather than treating them as a fourth, structurally separate
    /// alternative. A partitive denial ("I found none of the defects the objective describes")
    /// has no verb between "none" and the noun at all, so it gets its own alternative rather than
    /// stretching the bridging-verb grammar to cover a construction that isn't verb-shaped.
    /// </para>
    /// <para>
    /// The bridging-verb list named only the copula-like verbs a reviewer happened to use when
    /// this alternative was first written (cycle-9 verify finding, `ReviewVerdictValidation.cs:440`):
    /// "Nothing else in this delta introduced a defect.", "Nothing there survived verification as
    /// a defect." and "Nothing else in the delta raised a defect" all deny the same way "nothing
    /// is wrong" does — "nothing"/"none" as the sentence's own subject, denying that any defect
    /// exists — just with a verb the list did not enumerate, so they fell through to
    /// <see cref="DefectLanguagePattern"/> matching the bare noun and crediting a hollow verdict.
    /// "Introduced"/"raised"/"survived" join the list, drawn verbatim from recorded lens output
    /// rather than invented, the same discipline every other entry here already follows. The
    /// intervening-word cap widens from three to four words to reach "else in this/the delta",
    /// the modifier phrase separating "nothing" from its verb in two of the three recorded
    /// sentences — four is the exact width those two sentences need, not a round number picked
    /// for headroom — and it does not reopen the object-of-verb false positives above, since a
    /// verb reached only by walking past four unrelated words is never one this list enumerates.
    /// </para>
    /// <para>
    /// The subject-copula alternative's tail used to admit any text at all between the copula and
    /// its vocabulary word (PR #99 post-merge triage, task 29025f60): "nothing is logged, so the
    /// bug is invisible" states a real, located defect — nothing is logged — and only happens to
    /// use "bug" later, as the subject of an unrelated second clause describing the consequence,
    /// not as this alternative's own predicate. The old unrestricted <c>[^.!?]{0,40}</c> could not
    /// tell that from a genuine denial's own qualifying aside ("nothing is, in my judgment, a
    /// defect"), so it credited the false positive as a denial and discarded a real finding. What
    /// tells them apart is a comma introducing a fresh clause of its own: a genuine aside's commas
    /// bracket a parenthetical with no clause-opening conjunction after them ("in my judgment"),
    /// while the false positive's comma opens a new independent clause ("so the bug …") with its
    /// own subject and verb, unrelated to what "nothing" denies. The tail now refuses to cross a
    /// comma immediately followed by "so", the conjunction the recorded false positive actually
    /// uses — narrowly, the same discipline every other entry in this vocabulary already follows
    /// (drawn from an actual reported shape rather than a guessed-ahead list of every conjunction
    /// that could plausibly open such a clause): "because", "since" and the rest of that class are
    /// left as the same kind of accepted recall gap this file's other vocabulary lists already
    /// disclose, to be added if a real reviewer's phrasing ever files one.
    /// </para>
    /// <para>
    /// The comma-"so" exclusion above blocked a genuine denial's own comma-bounded aside that
    /// happens to open with "so" (independent pre-PR review, adversarial finding #3, task
    /// 29025f60): "Nothing here is, so far as I can tell, a defect." is the same kind of
    /// parenthetical as "in my judgment", just spelled with the one conjunction the exclusion
    /// blocks on. The exclusion now carves out "so far" and "so to speak" by name — the two
    /// idiomatic asides that open with "so" without introducing a fresh independent clause —
    /// rather than dropping the exclusion, which would reopen the false positive it exists for.
    /// </para>
    /// <para>
    /// That carve-out was itself incomplete (cycle-2 verify finding, task 29025f60): the finding
    /// it responded to named "so it seems" alongside "so far as I can tell" as the same class of
    /// idiomatic aside, but only "so far" and "so to speak" were carved out by name, leaving "so
    /// it seems" still read as a clause boundary and a defect like "Nothing here is, so it seems,
    /// a defect." still misread as naming a finding. The carve-out now also names "so it seems".
    /// </para>
    /// <para>
    /// The subject-copula tail's own clause-boundary guard was never applied to the older, looser
    /// second alternative below (independent pre-PR review, both lenses, task 29025f60): "nothing
    /// is escaped, so the path is broken" states a real, located defect — nothing is escaped — the
    /// same shape the guard above exists for, but the second alternative's unrestricted
    /// <c>[^.!?]{0,30}?</c> read straight past the ", so" clause boundary and matched the whole
    /// span as a denial anyway, discarding the defect. The same guard now applies here too.
    /// </para>
    /// <para>
    /// The screen was also narrower than the vocabulary it exists to guard (independent pre-PR
    /// review, both lenses, task 29025f60): a hollow verdict's own denial paragraph or sentence
    /// routinely uses a second negation phrased differently from every idiom above — "no doctrine
    /// is violated", "no acceptance criterion is unmet", "the diff does not depart from doctrine",
    /// "Nothing should change" — and <see cref="StatesDefectOutsideDenial"/> credits whatever of
    /// that second phrasing's own words (a bare "no", "not", "should", or the affirmative verb
    /// itself) happens to sit outside every alternative's matched span, reading a doubly-denied
    /// non-finding as a stated one. Three narrow alternatives close the recorded shapes: "no
    /// &lt;subject&gt; is/are violated/unmet", "does/do/did not depart", and a bare "nothing/none
    /// should" (the same subject-as-denial-target shape the existing alternatives already use, for
    /// the one recorded case where "should" itself is what a fixed vocabulary word would otherwise
    /// be). Each is deliberately as narrow as the phrasing it was drawn from, the same discipline
    /// this vocabulary's every other entry already follows; a denial phrased some other way is the
    /// same kind of accepted recall gap already disclosed above.
    /// </para>
    /// <para>
    /// All three of those new alternatives were themselves too wide (independent pre-PR review,
    /// both lenses, cycle 4, task 29025f60): each was drawn from a single-clause example and
    /// carried no guard against a real, located defect stated in one clause with its consequence,
    /// or the doctrine it violates, stated in a second. "`Sweep.cs:12` acquires no lock, so the
    /// invariant is violated." and "`Foo.cs:9` has no guard, so criterion 2 is unmet." are both
    /// genuine findings, but the "no … is/are violated/unmet" alternative's unrestricted tails read
    /// straight past the ", so" clause boundary the same way the subject-copula alternative's own
    /// tail once did, and matched the whole span as a denial. The same clause-boundary guard used
    /// there is applied here too, on both of this alternative's gaps. "`Foo.cs:12` does not seal
    /// the record, which departs from AGENTS.md." and "`Api.cs:7` does not validate the id and
    /// departs from the parameterize-identifiers rule." are also both genuine findings — "does not
    /// depart" is only the intended target when "not" and "depart" belong to the same clause, not
    /// when the reviewer's own sentence chains a second clause with "and" or "which" onto an
    /// unrelated "not" earlier in it — so the "does/do/did not depart" alternative now refuses to
    /// cross a comma or an "and"/"which"/"but" conjunction between them, the same discipline as the
    /// clause-boundary guard just above rather than a bare character-count widening. And "Nothing
    /// should be written before validation, but `Store.cs:40` writes first." is a genuine finding
    /// whose trailing "but" clause names a real defect the bare "nothing/none should" alternative
    /// otherwise reads straight past — the second alternative above already guards its own
    /// "wrong"/"broken"/"amiss" idiom against exactly this shape
    /// (<c>(?![^.!?]{0,20}\bbut\b)</c>), and this alternative gets the identical guard, widened to
    /// 40 characters to reach the recorded example's own "but".
    /// </para>
    /// <para>
    /// Every clause-boundary guard above named only the one connector its own recorded example
    /// happened to use (independent pre-PR review, both lenses, cycle 1, task 29025f60): "`Sweep.cs:12`
    /// acquires no lock; the invariant is violated.", "`Foo.cs:9` has no guard and criterion 2 is
    /// unmet." and "has no guard so criterion 2 is unmet." (no comma) are all the identical genuine
    /// finding the cycle-4 fix above already recognized for a comma-"so" boundary, just joined by a
    /// semicolon, a bare "and", or a bare "so" with no leading comma instead — none of which the
    /// "no … is/are violated/unmet" guard's <c>,\s*so\b</c> stopped at, so each read straight past the
    /// clause boundary and swallowed the real defect the same way the pre-cycle-4 guard did. The
    /// guard now also refuses to cross a semicolon or a bare "and", and treats "so" as a boundary
    /// whether or not a comma precedes it (still carving out "so far"/"so to speak"/"so it seems").
    /// The "does/do/did not depart" guard had the identical gap for a semicolon or a bare "so"
    /// ("`Foo.cs:12` does not seal the record; it departs from AGENTS.md.", "…does not seal the
    /// record so it departs…") despite already refusing a comma or "and"/"which"/"but", and gets the
    /// same two additions. The bare "nothing/none should" guard and its sibling "nothing/none …
    /// wrong/broken/amiss" alternative both named only "but" as a contrastive connector
    /// ("Nothing should be written before validation; `Store.cs:40` writes first.", "…validation,
    /// yet `Store.cs:40` writes first."), so both now also stop at "yet", "however", or a semicolon.
    /// And the two oldest subject-copula alternatives — the ones the cycle-4 fix above never
    /// touched — carried the original, narrower comma-"so" guard unchanged, the identical gap this
    /// paragraph closes everywhere else, so they get the same semicolon/bare-"and"/bare-"so"
    /// widening rather than being left one cycle behind the alternatives drawn from them.
    /// </para>
    /// <para>
    /// The semicolon and bare "and" widening just above were themselves too wide (independent
    /// pre-PR review, both lenses, cycle 3, task 29025f60): treating either as an impassable clause
    /// boundary unconditionally swallows a genuine compound denial that merely coordinates two
    /// negations rather than joining a defect to its consequence. "Nothing else in the delta
    /// introduced a regression, and I found no new defect in the surrounding code the fix touched."
    /// — a shape drawn from this install's own recorded lens output — uses "and" to join two
    /// negations about the same non-finding, not a defect and its doctrine fallout, but the widened
    /// guard's refusal to cross "and" left both "no" and "defect" uncovered, crediting the exact
    /// hollow verdict this file exists to reject. "and" now stays a boundary — refusing the match,
    /// exactly as before — only when it is not immediately followed by a continued negation
    /// ("no"/"not"/"nothing"/"none"/"n't", optionally after a leading "I found"); a boundary that
    /// genuinely joins two different clauses, like "`Foo.cs:9` has no guard and criterion 2 is
    /// unmet.", still stops at "and" exactly as the cycle-1 fix above intended, since nothing there
    /// continues the negation. The bare "so" addition carried an unrelated defect of its own:
    /// written with no leading word boundary, <c>,?\s*so\b</c> matched just as readily on the
    /// trailing "so" inside an unrelated word — "nothing here is also a defect" truncated its own
    /// tail one character short of "also", losing "defect" to a word that only happens to end in
    /// the same two letters. All four occurrences now anchor with <c>\bso\b</c>, matching the
    /// "does/do/did not depart" guard's own sibling instance below, which never had the gap.
    /// </para>
    /// <para>
    /// The trailing contrastive lookaheads on the "nothing/none … wrong/broken/amiss" and bare
    /// "nothing/none should" alternatives gained a bare semicolon in the same widening (independent
    /// pre-PR review, both lenses, cycle 3, task 29025f60), but unlike "but", "yet", and "however" —
    /// each reliably contrastive on its own — a semicolon just as often joins a denial to a
    /// restating elaboration as to a real second defect: "I found nothing wrong; every path
    /// disposes correctly.", "Nothing wrong here; the ordering is intentional.", "Nothing should
    /// change; the existing sealing is already correct.", and "Nothing should be reworked here; the
    /// naming is already right." are all genuine denials whose own semicolon-joined clause merely
    /// restates the same denial, and the bare semicolon check refused every one of them, crediting
    /// "wrong" or "should" as a stated defect the moment any semicolon followed within range. But a
    /// bare removal reopens the shape the semicolon was added for in the first place: "Nothing
    /// should be written before validation; `Store.cs:40` writes first." is the recorded finding
    /// that motivated adding it, and dropping the semicolon outright stops distinguishing it from
    /// the four affirming examples above. What actually separates them is not the semicolon itself
    /// but what follows it: the genuine finding's second clause names a location, the affirming
    /// elaborations never do. Both lookaheads now disqualify a semicolon only when a backtick-quoted
    /// reference follows it directly (<c>;\s*`</c>) — the same signal <see cref="CodeReferencePattern"/>
    /// already treats as "still describing a concrete artifact" elsewhere in this file — leaving an
    /// ordinary semicolon-joined restatement, which names nothing concrete, recognized as part of
    /// the same denial.
    /// </para>
    /// <para>
    /// The continued-negation carve-out's own "n't" alternative was unreachable (independent pre-PR
    /// review, both lenses, cycle 5, task 29025f60): sitting behind the shared <c>\s+</c> that leads
    /// every alternative in the list, it could only ever match the literal text "and n't", which no
    /// contraction produces — a real one attaches "n't" directly to the verb it negates ("doesn't",
    /// "isn't", "hasn't") with no space in between. "Nothing is wrong and it doesn't introduce a
    /// defect." and "Nothing is wrong and doesn't depart from doctrine." are exactly the compound
    /// denial this carve-out exists to recognize, but the "and" stayed an impassable boundary for
    /// both, truncating the match before "wrong" and crediting the trailing "defect"/"depart" as a
    /// stated finding. The alternative is now <c>\w*n't</c> rather than a bare <c>n't</c>, so it
    /// matches the whole contraction rather than an impossible standalone token, and the optional
    /// leading word before it grew from only "I found" to also allow a bare pronoun subject ("it"),
    /// the other shape this install's own recorded lens output uses ahead of a contraction.
    /// </para>
    /// <para>
    /// The backtick-immediately-after-semicolon spelling above was itself too narrow (independent
    /// pre-PR review, both lenses, cycle 5, task 29025f60): requiring the location to be the literal
    /// next character after the semicolon recognizes only one spelling of "the second clause names a
    /// location" and misses every other one a real reviewer actually writes — "Nothing should be
    /// written before validation; in `Store.cs:40` the write is first." (a lead-in word before the
    /// backtick), "Nothing should be written before validation; Store.cs:40 writes first." (no
    /// backticks at all, the exact spelling the `at=` finding contract itself uses), and "Nothing
    /// should be reworked here; **`Store.cs:40`** writes first." (bold-and-backtick) all read as a
    /// semicolon-joined restatement and were discarded as a denial. The other direction had the
    /// opposite problem: any backtick-quoted token disqualified the semicolon, whether or not it was
    /// actually a location, so "I found nothing wrong; `Dispose` runs on every path." — a bare
    /// symbol with no extension or line number, naming nothing concrete — read as a real second
    /// clause and leaked the denial as a stated defect. Both lookaheads now require the same
    /// location shape <see cref="LocationPattern"/> recognizes (allowing an optional leading "in "
    /// and up to three <c>`</c>/<c>*</c> decorators before it, since a real reviewer's location is
    /// usually backticked or bolded rather than bare) immediately after the semicolon, rather than a
    /// bare backtick: a match only disqualifies the semicolon when what follows it is actually a
    /// file-shaped location, not merely quoted. This narrowing is not a closed classifier either —
    /// an affirming elaboration that happens to name a real file, "Nothing wrong here;
    /// `ReviewEngine.cs` orders the catch blocks intentionally.", now reads as a stated defect the
    /// same way a genuine finding does, the same keyword-and-proximity limit this file's own doc
    /// comments disclose elsewhere ("closing that gap needs reading comprehension a regex cannot
    /// do"); trading that rarer false credit for closing the far more common false discard above is
    /// the same precision/recall call the <see cref="LocationPattern"/> narrowing above made.
    /// </para>
    /// <para>
    /// Both semicolon-disqualifier lookaheads only ever replicated <see cref="LocationPattern"/>'s
    /// generic `word.ext[:line]` alternative, not the other two (cycle-6 verify finding,
    /// `ReviewVerdictValidation.cs:673`), despite this doc comment's own "same location shape
    /// <see cref="LocationPattern"/> recognizes" claim: a second clause naming a bare-conventional
    /// filename ("Nothing should be added; Dockerfile needs a HEALTHCHECK.") or a dotfile
    /// ("Nothing should change; .gitignore already excludes it.") has no `.ext` suffix, so the old
    /// disqualifier never recognized it as a location, and the whole sentence was credited as a
    /// denial the same way the pre-fix `Store.cs:40` case was — a genuine finding discarded, not
    /// the narrower false-credit trade-off the paragraph above discloses. <see cref="LocationShape"/>
    /// now shares the literal three-alternative shape with <see cref="LocationPattern"/> so the two
    /// can no longer drift apart the way they did here.
    /// </para>
    /// <para>
    /// That sharing was itself only a copy at first (cycle-7 verify finding,
    /// `ReviewVerdictValidation.cs:707`): the copy dropped <see cref="LocationPattern"/>'s
    /// non-word-boundary before the dotfile alternative and the word boundaries after the dotfile
    /// and bare-conventional-filename alternatives, so it matched strictly more text than
    /// <see cref="LocationPattern"/> itself ever recognizes as a location — "Dockerfiles" and
    /// ".gitignored" both matched the copy as a prefix even though neither is a location
    /// <see cref="LocationPattern"/> would credit, which let a semicolon-joined clause like
    /// "Nothing should change; Dockerfiles need updating for the new base image." disqualify the
    /// denial over a location that was never actually named. <see cref="LocationShape"/> is now
    /// the one literal <see cref="LocationPattern"/> itself compiles from (via its own
    /// <c>[GeneratedRegex(LocationShape)]</c>) rather than a hand-copied duplicate, which is what
    /// actually keeps the two from drifting apart — a shared literal cannot itself drift, where a
    /// second hand-copy always risks it again the next time either one changes.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(?:nothing|none)\b(?:\s+\w+){0,4}?\s+(?:is|are|was|were|stands?|remains?|exists?|"
        + @"qualif(?:y|ies)(?:\s+as)?|amounts?\s+to|counts?\s+as|worth\s+calling|"
        + @"introduced|raised|survived)\b"
        + @"(?:(?!;|\band\b(?!\s+(?:i\s+found\s+|it\s+)?(?:no|not|nothing|none|\w*n't)\b)|,?\s*\bso\b(?!\s+(?:far\b|to\s+speak\b|it\s+seems\b)))[^.!?]){0,40}"
        + @"\b(?:wrong|broken|amiss|defects?|bugs?|issues?|problems?)\b"
        + @"|\b(?:nothing|none)\b(?:(?!;|\band\b(?!\s+(?:i\s+found\s+|it\s+)?(?:no|not|nothing|none|\w*n't)\b)|,?\s*\bso\b(?!\s+(?:far\b|to\s+speak\b|it\s+seems\b)))[^.!?]){0,30}?"
        + @"\b(?:wrong|broken|amiss)\b(?![^.!?]{0,20}(?:\bbut\b|\byet\b|\bhowever\b|"
        + @";\s*(?:in\s+)?[`*]{0,3}" + LocationShape + @"))"
        + @"|\b(?:nothing|none)\b\s+of\s+the\s+(?:defects?|bugs?|issues?|problems?)\b"
        + @"|\bno\b[^.!?]{0,10}\b(?:defects?|bugs?|issues?|problems?)\s+(?:stands?|remains?|exists?|found)\b"
        + @"|\bno\b(?:(?!;|\band\b(?!\s+(?:i\s+found\s+|it\s+)?(?:no|not|nothing|none|\w*n't)\b)|,?\s*\bso\b(?!\s+(?:far\b|to\s+speak\b|it\s+seems\b)))[^.!?]){0,30}?"
        + @"\b(?:is|are)\b(?:(?!;|\band\b(?!\s+(?:i\s+found\s+|it\s+)?(?:no|not|nothing|none|\w*n't)\b)|,?\s*\bso\b(?!\s+(?:far\b|to\s+speak\b|it\s+seems\b)))[^.!?]){0,10}?"
        + @"\b(?:violat(?:es?|ed)|unmet)\b"
        + @"|\b(?:does|do|did)\s+not\b(?:(?!,|;|\bso\b(?!\s+(?:far\b|to\s+speak\b|it\s+seems\b))|\b(?:and|which|but)\b)[^.!?]){0,30}?\bdeparts?\b"
        + @"|\b(?:nothing|none)\b\s+should\b(?![^.!?]{0,40}(?:\bbut\b|\byet\b|\bhowever\b|"
        + @";\s*(?:in\s+)?[`*]{0,3}" + LocationShape + @"))",
        RegexOptions.IgnoreCase)]
    private static partial Regex HeadingDenialPattern();

    /// <summary>
    /// The location shape <see cref="LocationPattern"/> itself compiles from and the two
    /// semicolon-disqualifier lookaheads in <see cref="HeadingDenialPattern"/> require after the
    /// semicolon: one literal both use, rather than a hand-copied duplicate that can drift away
    /// from <see cref="LocationPattern"/>'s own three alternatives the way two earlier copies each
    /// did in turn (cycle-6 verify finding, `ReviewVerdictValidation.cs:673`, dropped the dotfile
    /// and bare-conventional-filename alternatives entirely; cycle-7 verify finding,
    /// `ReviewVerdictValidation.cs:707`, copied all three but without the non-word-boundary before
    /// the dotfile alternative or the word boundaries after the dotfile and bare-filename
    /// alternatives, so it matched more text as a location than <see cref="LocationPattern"/>
    /// itself would — "Dockerfiles" and ".gitignored" both matched as a `Dockerfile`/`.gitignore`
    /// prefix even though neither is a location <see cref="LocationPattern"/> recognizes).
    /// </summary>
    private const string LocationShape =
        @"(?:[\w/\\-]*[A-Za-z0-9_-]\.[a-z]{2,10}(:\d+)?"
        + @"|\B\.(?:gitignore|gitattributes|gitmodules|dockerignore|editorconfig|env|npmrc|nvmrc)(?::\d+)?\b"
        + @"|\b(?:Dockerfile|Makefile|Jenkinsfile|Gemfile|Rakefile|Procfile|Vagrantfile)(?::\d+)?\b)";

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
    /// <paramref name="taskAgentContext"/> screens the identical class of echo for the task's own
    /// agent context — but only for a pr-review task's conformance lens
    /// (<c>PrReviewEngine.HasUsableVerdict</c> is this parameter's only caller now; the ordinary
    /// pre-PR loop's own <c>ReviewEngine.RecordReviewPassAsync</c> never passes it, verify cycle-2
    /// finding): the pull request's own title and description live nowhere else for that lens to
    /// read, so <c>AgentPromptBuilder.BuildConformanceReview</c> prints agent context into its own
    /// prompt's "## Context" section only on its <c>DiffIsForeignPullRequest</c> branch, right
    /// alongside the objective and the acceptance criteria that branch also carries there
    /// (cycle-2 review, adversarial finding). A routed bug task's agent context embeds a prior
    /// review finding verbatim — header, location and all — and an ordinary adopted task's
    /// context is an issue body that routinely pairs a filename with defect vocabulary the same
    /// way a criterion does, so a session that restates either before concluding satisfies the
    /// location-plus-defect shape below without having found anything, for the identical reason
    /// restating the objective does. Unlike the objective, an agent context is routinely hundreds
    /// to thousands of characters — <c>WorkItemContext.Compose</c>'s provenance header, framing
    /// paragraph and fenced item body — so a whole-string match the way the objective is screened
    /// almost never fires (cycle-1 adversarial finding, `ReviewVerdictValidation.cs:497`): a
    /// session restating one paragraph of it produces no span equal to the entire context.
    /// <see cref="AgentContextParagraphs"/> splits it on the same blank-line boundary
    /// <see cref="ParagraphBoundary"/> already uses to separate one finding's text from the next,
    /// so each paragraph — the header lines, the framing sentence, and each paragraph of the
    /// quoted body, a routed bug task's embedded prior finding included — is screened as its own
    /// snippet through <see cref="StripVerbatimEchoes"/>, the same way each acceptance-criteria
    /// bullet already is.
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
        IReadOnlyList<string>? priorRulingReasons = null, string? taskAgentContext = null)
    {
        if (output.IsBlank())
        {
            return false;
        }

        // Normalized before any strip runs, not only afterward for the paragraph split below
        // (cycle-3 conformance finding): AgentContextParagraphs hands StripVerbatimEchoes needles
        // already normalized to '\n', so a CRLF-authored pass's raw text has to match on the same
        // terms, or a multi-line needle — the agent-context ones are the only multi-line needles
        // this screen strips — never lines up against it and the echo it is meant to catch survives.
        string normalized = output.Replace("\r\n", "\n");
        string sanitized = StripVerbatimEchoes(
            StripVerbatimEchoes(
                StripVerbatimEchoes(
                    StripObjectiveEcho(StripPlaceholderLocations(normalized), taskObjective), taskAcceptanceCriteria),
                priorRulingReasons),
            AgentContextParagraphs(taskAgentContext));

        IReadOnlyList<ReviewFinding> structuredFindings = ReviewResultParser.ParseFindings(sanitized);
        if (structuredFindings.Any(HasStatedDefect))
        {
            return true;
        }

        string body = string.Join('\n', sanitized
            .Split('\n')
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
    /// <para>
    /// Credits on <see cref="StatesDefectOutsideDenial"/> rather than a bare
    /// <c>DefectLanguagePattern().IsMatch(candidate)</c>, and checks it before the denial check
    /// stops the walk, not after (PR #99 post-merge triage, task 29025f60, found sweeping this
    /// same-shaped defect's sibling sites): the same whole-span veto <see cref="NamesFindingInProse"/>'s
    /// sentence-scoped check used to make — discarding an entire span the instant
    /// <see cref="HeadingDenialPattern"/> matched anywhere in it — applied here at paragraph scope
    /// too, so a candidate paragraph stating a real defect in one clause and denying a second,
    /// unrelated one in another ("The limiter never resets after a failed login; nothing else about
    /// it is wrong.") had its own denial phrase stop the walk before the real defect earlier in the
    /// same paragraph was ever credited. A paragraph that denies and states nothing else still
    /// stops the walk exactly as before, since <see cref="StatesDefectOutsideDenial"/> returns
    /// false for it and the denial check below still fires.
    /// </para>
    /// <para>
    /// <see cref="StructuralMarkerPattern"/> is checked after the denial check, not folded into the
    /// same <c>||</c> as <see cref="StatesDefectOutsideDenial"/> above it (independent pre-PR
    /// review, adversarial finding, cycle 1, task 29025f60): a candidate that only denies, but
    /// happens to carry the structured contract's own `Scenario:` label anyway ("Scenario: nothing
    /// is wrong; the loop is correct.") used to credit a hollow verdict the instant the label
    /// matched, before the denial check on the next line ever ran — the identical whole-span-veto
    /// class of bug the cycle-4 fix above closed for <see cref="StatesDefectOutsideDenial"/> itself,
    /// reopened here for the structural marker by folding the two into the same disjunction. (A
    /// `Defect:` label rather than `Scenario:` never actually reaches this gap, because the bare
    /// word "defect" is itself in <see cref="DefectLanguagePattern"/>'s own vocabulary and sits
    /// before any denial phrase that follows it, so <see cref="StatesDefectOutsideDenial"/> already
    /// credits it on the line above regardless of this ordering — "Scenario:" carries no such word
    /// of its own, which is what let this gap through.) The marker still credits a candidate that
    /// carries no real defect language of its own but is not a denial either — the label alone is
    /// still a signal worth trusting there — it just no longer overrides a denial this same
    /// candidate already stated.
    /// </para>
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

            if (StatesDefectOutsideDenial(candidate))
            {
                return true;
            }

            if (HeadingDenialPattern().IsMatch(candidate))
            {
                return false;
            }

            if (StructuralMarkerPattern().IsMatch(candidate))
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
    /// The task's own agent context, split into <see cref="StripVerbatimEchoes"/> snippets on the
    /// same blank-line boundary <see cref="ParagraphBoundary"/> already splits a reviewer's own
    /// output on — see <see cref="NamesAFinding"/>'s <c>taskAgentContext</c> doc for why a single
    /// whole-string needle (the way <see cref="StripObjectiveEcho"/> screens the much shorter
    /// objective) misses everything but the shortest contexts.
    /// <para>
    /// A fence delimiter line (<c>WorkItemContext.Compose</c>'s and <c>ReviewDraftBugTask</c>'s own
    /// <c>RelayedText.FenceFor</c> line, three or more backticks alone on their line) is blanked
    /// before the split, not left as ordinary text: both composers write it glued directly to the
    /// quoted body with no blank line of its own — `` ``` `` immediately followed by the body's
    /// first line, and the body's last line immediately followed by the closing `` ``` `` — so
    /// without this, the quoted body's first and last paragraphs (its only paragraph, for a short
    /// one-paragraph body — exactly the shape <see cref="ReviewDraftBugTask"/>'s routed finding
    /// text is) carry the fence as part of their own needle and never match a reviewer's echo of
    /// the body alone, fence-free. Blanking the fence line turns it into the blank line the
    /// composer never wrote, letting the boundary the composer meant fall out on its own.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string>? AgentContextParagraphs(string? taskAgentContext)
    {
        if (taskAgentContext.IsBlank())
        {
            return null;
        }

        // Normalized the same way NamesAFinding normalizes a reviewer's own output: a CRLF-authored
        // context (a task.md read on Windows, or an adopted issue body copied in verbatim) leaves a
        // stray '\r' between the two '\n's ParagraphBoundary and FenceDelimiterLine need to see a
        // blank line, collapsing every paragraph — and the fence delimiter itself — back into one.
        string normalized = string.Join(
            '\n', taskAgentContext.Split('\n').Select(line => line.TrimEnd('\r')));
        return ParagraphBoundary().Split(FenceDelimiterLine().Replace(normalized, string.Empty));
    }

    /// <summary>A markdown fence delimiter alone on its own line, with nothing else on it.</summary>
    [GeneratedRegex(@"^[ \t]*`{3,}[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex FenceDelimiterLine();

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
    /// Split at a markdown bullet marker first, one bullet item's own text at a time, and only
    /// then at <see cref="SentenceBoundary"/> within each bullet (task a77503ff, origin: task
    /// f39bff24's PR #49 review cycle 3, both lenses independently): <see cref="SentenceBoundary"/>
    /// alone cannot break at a bare bullet, so a tight list — each item its own complete finding,
    /// a location and its defect stated together — used to arrive here as one merged "sentence"
    /// spanning every item, wrongly demoting a real finding. Splitting on the bullet marker first
    /// keeps each item's own text — and only its own text — in one "sentence", so a self-contained
    /// bullet finding is credited on the same-sentence check exactly as an ordinary prose finding
    /// would be. <see cref="EmbeddedListMarkerPattern"/> captures the marker it splits on (cycle-1
    /// pre-PR review on this very change) rather than consuming it, so it survives as its own
    /// element between one bullet's sentences and the next's; <see cref="StatesDefectWithinLookahead"/>'s
    /// own guard against that same pattern is what stops its walk the moment it reaches one, so a
    /// location in one item still cannot borrow defect language from an unrelated next item, and
    /// the lookahead below still requires <see cref="ContinuationPattern"/> or
    /// <see cref="CodeReferencePattern"/> on top of that to credit anything past the boundary. The
    /// same-sentence check below screens against <see cref="HeadingDenialPattern"/> rather than
    /// against <see cref="EmbeddedListMarkerPattern"/> itself (cycle-3 pre-PR review, both lenses):
    /// once a bullet's own text is isolated this way, no element that reaches this check can ever
    /// contain a list marker (a bare marker element carries no location, so it never passes the
    /// <see cref="LocationPattern"/> gate above), which made the marker guard here permanently
    /// unreachable and left one bullet item's own denial idiom ("`Foo.cs:10` — the catch order is
    /// correct; nothing is wrong here.") credited as a finding purely because "wrong" shares its
    /// sentence with the location — the same shape <see cref="HeadingDenialPattern"/> already
    /// screens for the heading branch and the backward-pointer branch just below.
    /// </para>
    /// <para>
    /// Both branches below veto on <see cref="StatesDefectOutsideDenial"/> rather than a bare
    /// <c>!HeadingDenialPattern().IsMatch(...)</c> (PR #99 post-merge triage, task 29025f60): the
    /// old whole-sentence veto discarded the entire sentence the instant
    /// <see cref="HeadingDenialPattern"/> matched anywhere in it, even when the same sentence had
    /// already stated a real, located defect before the denial clause — "`Auth.cs:42` never resets
    /// the limiter; nothing else is wrong." names the limiter defect in its first clause and only
    /// denies a second, unrelated one in its last few words, but the old check read the trailing
    /// denial and threw the whole sentence away, defect included. Scoping the veto to the denial's
    /// own matched span, rather than the whole sentence, lets a defect stated outside that span
    /// still be credited.
    /// </para>
    /// </summary>
    private static bool NamesFindingInProse(string paragraph)
    {
        string[] sentences = [.. EmbeddedListMarkerPattern().Split(paragraph)
            .SelectMany(bullet => SentenceBoundary().Split(bullet))];
        for (int index = 0; index < sentences.Length; index++)
        {
            if (!LocationPattern().IsMatch(sentences[index]))
            {
                continue;
            }

            if (StatesDefectOutsideDenial(sentences[index]) || StatesDefectWithinLookahead(sentences, index))
            {
                return true;
            }

            string? previous = index > 0 ? sentences[index - 1] : null;
            if (previous is not null
                && BackwardPointerPattern().IsMatch(sentences[index])
                && StatesDefectOutsideDenial(previous))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> states a defect somewhere <see cref="HeadingDenialPattern"/>'s
    /// own match does not cover, rather than only inside it (PR #99 post-merge triage, task
    /// 29025f60): a bare <c>DefectLanguagePattern().IsMatch(text) &amp;&amp;
    /// !HeadingDenialPattern().IsMatch(text)</c> vetoes the whole span — a sentence for
    /// <see cref="NamesFindingInProse"/>'s two callers, a whole paragraph for
    /// <see cref="StatesDefectInLaterParagraph"/>'s — the moment any denial idiom matches anywhere
    /// in it, which is right for a span that denies and nothing else, but wrong for one that states
    /// a real defect in one clause and denies a second, unrelated one in another — the denial match
    /// covers only its own words, so defect language sitting outside that span (before it or after
    /// it) still names something. This is still a keyword-and-proximity check, not a semantic one:
    /// coverage is decided by the defect match's own start index falling inside a denial match's
    /// span, not by any true overlap test, so a defect match that starts before a denial match and
    /// only extends into it would not be caught by this check (independent pre-PR review,
    /// adversarial finding, task 29025f60, correcting this comment's own prior overstatement of
    /// what is implemented). No input reaches that gap today: every <see cref="HeadingDenialPattern"/>
    /// alternative anchors at its own negator word, and every <see cref="DefectLanguagePattern"/>
    /// match is word-bounded, so a defect match can never start before the denial match it would
    /// need to overlap — but a future vocabulary addition to either pattern could change that.
    /// </summary>
    private static bool StatesDefectOutsideDenial(string text)
    {
        List<Match> denialMatches = [.. HeadingDenialPattern().Matches(text).Cast<Match>()];
        foreach (Match defect in DefectLanguagePattern().Matches(text))
        {
            bool coveredByDenial = denialMatches.Any(
                denial => defect.Index >= denial.Index && defect.Index < denial.Index + denial.Length);
            if (!coveredByDenial)
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
    /// <para>
    /// Credits on <see cref="StatesDefectOutsideDenial"/>, checked before the denial check stops
    /// the walk rather than after (PR #99 post-merge triage, task 29025f60, found sweeping this
    /// same-shaped defect's sibling sites): a lookahead sentence that states a real defect in one
    /// clause and denies a second, unrelated one in another used to have its own trailing denial
    /// stop the walk before the real defect earlier in that same sentence was ever credited, the
    /// identical whole-span veto <see cref="NamesFindingInProse"/>'s same-sentence check and
    /// <see cref="StatesDefectInLaterParagraph"/>'s forward walk both made.
    /// </para>
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

            if (StatesDefectOutsideDenial(candidate))
            {
                return true;
            }

            if (HeadingDenialPattern().IsMatch(candidate))
            {
                return false;
            }
        }

        return false;
    }
}
