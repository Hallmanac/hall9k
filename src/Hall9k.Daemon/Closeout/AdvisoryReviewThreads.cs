using System.Text.RegularExpressions;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Which unresolved threads a person opened are asking this pull request for nothing at all
/// (task: a review-feedback follow-up never answers a human reviewer in the owner's name on its
/// own). The FYI beside an approval: "nice, my own pull request needs this too".
/// <para>
/// Origin incident, arx-platform PR #2021 on 2026-09-09: John Mark approved at 10:11 and opened
/// one thread saying his own pull request needed the same pattern; closeout read one unresolved
/// human thread at 10:14 and reopened the task; the follow-up correctly declined it and then
/// posted about 400 words under Brian's login at 10:16, which he deleted at 10:21. The lap
/// should never have been bought. What makes it safe not to buy is narrow and stated twice
/// below: the thread has to ask nothing, AND the person who wrote it has to have no standing
/// request for change on the pull request.
/// </para>
/// <para>
/// <b>Which way this errs, and why.</b> Every judgment here defaults to dispatching. A thread
/// this cannot read, a reviewer whose verdict this cannot see, a body the provider never
/// reported — none of them is ever advisory, which is the behaviour that existed before this
/// class did. Neither is a thread that reports a defect in plain indicative form, which asks for
/// a fix without asking anything (<see cref="DefectClaimPhrases"/>), nor one that states the
/// change itself as a bare imperative, which is the ordinary shape of a nit
/// (<see cref="ImperativeVerbs"/>). The cost of a
/// false advisory is a person's question going unanswered until somebody notices by hand; the
/// cost of a false ask is one lap that drafts a reply and parks. Those are not the same size, so
/// the test is deliberately easy to fail.
/// </para>
/// </summary>
internal static class AdvisoryReviewThreads
{
    /// <summary>
    /// Phrasings that ask this pull request's author to do or to say something. Case-insensitive,
    /// anchored at the START of a word and open-ended at the other end, and deliberately not
    /// exhaustive: a request this list misses still has to get past
    /// <see cref="AsksSomething"/>'s question-mark test and <see cref="DefectClaimPhrases"/>.
    /// <para>
    /// Open-ended on purpose, so "consider" catches "considering" and "suggest" catches
    /// "suggestion" — both are still asks. Anchored at the start so a phrase buried inside a
    /// longer word is not one: without it "nit:" is fine but "prefer" fires on "preferred" in
    /// somebody else's quoted code, which errs in the safe direction but for no reason anyone
    /// could explain.
    /// </para>
    /// <para>
    /// What is NOT here matters as much. Bare "need"/"needs" is excluded on purpose — "my own PR
    /// needs the same pattern" is the origin incident's exact wording and is a remark about
    /// somebody else's branch, not a request on this one; "needs to"/"need to" are kept, since
    /// those read as a requirement on the thing under review. Bare verbs a reviewer uses
    /// descriptively ("this removes the cast", "we use the sentinel here") are excluded for the
    /// same reason: matching them would classify almost every thread as an ask and quietly turn
    /// this whole exception off.
    /// </para>
    /// </summary>
    private static readonly string[] AskPhrases =
    [
        "please",
        "can you", "could you", "would you", "can we", "could we", "should we", "shall we",
        "should this", "should it", "should they", "should be", "should have", "should probably",
        "needs to", "need to", "has to be", "have to be", "must be", "ought to",
        "let's", "lets just",
        "consider", "suggest", "recommend", "prefer",
        "what about", "how about", "why not", "what if", "wdyt", "thoughts",
        "nit:", "nit -", "blocking:", "todo:",
    ];

    /// <summary>
    /// Phrasings that CLAIM something is wrong with the code under review. A defect report asks
    /// for a fix without the courtesy of asking: "This is off by one: the loop starts at 1." and
    /// "This throws when the list is empty." are the most ordinary shape a substantive inline
    /// review comment takes, and neither carries a question mark or any of
    /// <see cref="AskPhrases"/>. Treating them as remarks left a real defect report un-actioned
    /// until somebody noticed by hand (independent pre-PR review, cycle 1, adversarial lens).
    /// <para>
    /// Matched the same way <see cref="AskPhrases"/> is — anchored at a word start, open-ended at
    /// the other end — which is what keeps "hang" off "changed" and "race" off "traced" while
    /// still catching "hangs", "racy", "throwing", "duplication".
    /// </para>
    /// <para>
    /// This is claim vocabulary, not negation. Bare "not"/"never"/"no longer" are deliberately
    /// absent: "this is the bit I could never get right, good catch" is a remark, and matching
    /// plain negation would classify nearly every thread as an ask and quietly turn the whole
    /// advisory exception off, which is the same trap the note above about bare descriptive verbs
    /// names. The negated forms that ARE here are phrase-level assertions of malfunction
    /// ("doesn't work", "does not handle"), which a remark does not reach for.
    /// </para>
    /// </summary>
    private static readonly string[] DefectClaimPhrases =
    [
        "bug", "broken", "breaks", "breaking", "broke", "regress",
        "wrong", "incorrect", "off by one", "off-by-one", "backwards", "inverted",
        "throw", "crash", "hang", "deadlock", "race", "leak", "overflow", "underflow",
        "out of bounds", "off the end", "fail",
        "typo", "misspell", "dead code", "unreachable", "duplicat", "missing",
        "swallow", "silently", "mismatch", "inconsistent", "unsafe", "stale",
        "doesn't work", "does not work", "doesn't handle", "does not handle",
        "doesn't match", "does not match", "won't work", "will not work",
    ];

    /// <summary>
    /// The bare imperative, which is the shape a nit beside an approval most often takes:
    /// "Rename this to <c>Canonical</c>.", "Add a null check here.", "Use the sentinel instead."
    /// No question mark, no request phrasing, no defect claim — and before this list a thread like
    /// that was classified advisory, so the lap that used to fix it stopped being dispatched while
    /// the unresolved thread went on holding the pre-approved merge (independent pre-PR review,
    /// cycle 1, adversarial lens).
    /// <para>
    /// Matched only in the IMPERATIVE POSITION — the start of the text, or the start of a later
    /// sentence, bullet, or quoted line (<see cref="ImperativePattern"/>) — which is what
    /// separates the ask from the description. "Use the sentinel instead" opens a sentence;
    /// "we use the sentinel here" does not, and "this renames the column" is a different word
    /// entirely, since every entry is matched in its bare form with a word-end guard so the
    /// inflected third-person ("renames", "adds") never fires. Without the position test this
    /// list would match nearly every thread and turn the whole advisory exception off, which is
    /// the same trap <see cref="AskPhrases"/>'s own doc names for bare descriptive verbs.
    /// </para>
    /// <para>
    /// Bare "never" is deliberately absent, for the reason <see cref="DefectClaimPhrases"/> leaves
    /// plain negation out: "Never mind, I misread this" is a reviewer retracting, and it is common
    /// enough that matching it would buy laps for nothing. The unambiguous imperative negations
    /// ("don't", "do not") are here.
    /// </para>
    /// <para>
    /// "Merge" is absent for a narrower reason: this same test reads a reviewer's own review BODY
    /// (<see cref="ReviewerAsksNothing"/>), and "Merge when green" is one of the commonest ways an
    /// approval is worded. "Combine", "fold" and "collapse" carry the code request it would
    /// otherwise have covered.
    /// </para>
    /// </summary>
    private static readonly string[] ImperativeVerbs =
    [
        "add", "remove", "delete", "drop", "rename", "move", "extract", "inline",
        "replace", "swap", "use", "reuse", "make", "change", "update", "split",
        "combine", "wrap", "guard", "seal", "document", "simplify", "revert",
        "fix", "rework", "rewrite", "reorder", "narrow", "widen", "tighten", "clarify",
        "reword", "rephrase", "pass", "handle", "check", "validate", "assert", "avoid",
        "keep", "put", "switch", "convert", "unify", "dedupe", "deduplicate", "extend",
        "expose", "inject", "pin", "bump", "fold", "collapse", "hoist", "mark",
        "annotate", "restrict", "initialize", "dispose", "cancel",
        "don't", "dont", "do not",
    ];

    private static readonly Regex AskPhrasePattern = Anchored(AskPhrases);

    private static readonly Regex DefectClaimPattern = Anchored(DefectClaimPhrases);

    /// <summary>
    /// One of <see cref="ImperativeVerbs"/> where an imperative can stand: at the very start of
    /// the text, on a fresh line, or after a sentence terminator — past whatever markdown
    /// decoration a reviewer opened the line with (a bullet, a quote marker, a list number, a
    /// backtick). Closed with a word-end guard so only the bare verb matches, never an inflection
    /// of it.
    /// <para>
    /// A sentence terminator only counts with whitespace behind it, which is what keeps a member
    /// access out of the imperative position: <c>thread.Add(x)</c> and <c>config.Update(…)</c> are
    /// code a reviewer quoted, not a sentence beginning "Add" or "Update", and a bare
    /// terminator class would have read every thread quoting a method call as an ask.
    /// </para>
    /// </summary>
    private static readonly Regex ImperativePattern = new(
        @"(?:^|\r?\n|[.!?;:][ \t]+)[\s>*\-+`_""'\[(]*(?:\d+[.)][ \t]*)?(?:"
            + string.Join('|', ImperativeVerbs.Select(Regex.Escape))
            + @")(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static Regex Anchored(IEnumerable<string> phrases) => new(
        string.Join('|', phrases.Select(phrase => $@"(?<!\w){Regex.Escape(phrase)}")),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Whether this text asks the pull request's author for a change or an answer — including the
    /// two kinds that ask without asking: a defect report, which states a malfunction, and a bare
    /// imperative, which states the change itself. A question mark is the strong signal and stands
    /// alone; everything else is <see cref="AskPhrases"/>, <see cref="DefectClaimPhrases"/>, or
    /// <see cref="ImperativeVerbs"/> in the imperative position. Blank text asks nothing it can be
    /// read to ask — but a
    /// blank thread body never reaches <see cref="Advisory"/> as advisory on its own, because the
    /// reviewer-side test still has to pass, and text that is missing rather than blank never
    /// reaches this at all: <see cref="Advisory"/> requires a readable opener ahead of asking
    /// this, precisely because "unreadable" would arrive here indistinguishable from "silent".
    /// </summary>
    internal static bool AsksSomething(string? text) =>
        text.IsNotBlank()
        && (text.Contains('?')
            || AskPhrasePattern.IsMatch(text)
            || DefectClaimPattern.IsMatch(text)
            || ImperativePattern.IsMatch(text));

    /// <summary>
    /// The unresolved human threads on this snapshot that buy no follow-up lap: the thread asks
    /// nothing, and the person who opened it has no standing request for change on the pull
    /// request and asked for none in the words of their own latest review.
    /// <para>
    /// The reviewer test is deliberately the STANDING verdict rather than the latest review's
    /// state, the same distinction <see cref="PullRequestReviewer.RequestedChanges"/> was written
    /// for and #152 was corrected by: GitHub wraps a plain thread reply in an implicit COMMENTED
    /// review, so a reviewer who requested changes and then replied once has a comment as their
    /// latest while their verdict goes on blocking the merge. Reading the latest would classify
    /// that reviewer's next remark as advisory and leave a standing request for change
    /// undispatched. (In practice a standing CHANGES_REQUESTED sends the whole pull request down
    /// the <c>ReviewRequestedChanges</c> branch before this is consulted at all; the test is here
    /// so this class is safe read on its own, not only where it happens to be called from.)
    /// </para>
    /// <para>
    /// A thread whose opening comment the provider never reported is NOT advisory either, which is
    /// where this class's own "a body the provider never reported comes back asks something"
    /// promise is actually kept: <see cref="AsksSomething"/> reads an unreadable body as asking
    /// nothing, so the readability test has to sit here, ahead of it. Null is that unknown;
    /// an empty string is a body the provider reported as empty, and stays advisory-eligible on
    /// the reviewer-side test alone (Copilot, PR #397).
    /// </para>
    /// <para>
    /// A thread whose opener matches no reviewer the snapshot lists is NOT advisory: the pull
    /// request's own author is filtered out of <c>Reviewers</c> deliberately
    /// (<c>GitHubPullRequestInspector.ReadReviewers</c>), so a self-review note lands here
    /// unmatched, and the honest reading of "no verdict observed for this person" is that their
    /// standing is unknown rather than approving.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Advisory(PullRequestSnapshot snapshot) =>
    [
        .. snapshot.HumanThreads
            .Where(thread => thread.OpeningComment is { } opening
                && !AsksSomething(opening)
                && ReviewerAsksNothing(snapshot, thread.Author))
            .Select(thread => thread.ThreadId),
    ];

    private static bool ReviewerAsksNothing(PullRequestSnapshot snapshot, string author) =>
        snapshot.Reviewers.FirstOrDefault(reviewer =>
            string.Equals(reviewer.Login, author, StringComparison.OrdinalIgnoreCase)) is { } reviewer
        && !reviewer.RequestedChanges
        && !AsksSomething(reviewer.LatestReviewBody);
}
