using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// What the daemon writes into a pull request. Its own type because it is the one artifact of a
/// run that outlives Hall9k: reviewers read it in GitHub, long after the run directory is gone.
/// </summary>
internal static class PullRequestBody
{
    /// <summary>
    /// What the pull request is called: the title the build session composed for itself, and the
    /// task's objective only when no session composed one.
    /// <para>
    /// Both are relayed text and both get the same defusal, which the title needs more urgently
    /// than the body does. The objective of an adopted task is an issue title someone else wrote
    /// (PLAN.md §3.1a), and a title looks like the safest place for it while being the most
    /// dangerous: GitHub's default squash-merge commit message <em>is</em> the pull request title,
    /// so an adopted issue called "Fix login, resolves #500" would close issue 500 the moment this
    /// merged, having never appeared in the body the defusal guards.
    /// </para>
    /// <para>
    /// It is folded to one printable line first. A title is a commit subject by the time it
    /// matters, and a newline or an escape sequence in one lands in the repository's history and
    /// in every terminal that later runs git log. Which characters those are is
    /// <see cref="RelayedText"/>'s rule, the same one the CLI asks at the other sink: the daemon
    /// cannot reference the CLI (AGENTS.md, reference graph), and a second hand-written list of
    /// bidirectional controls is a list that drifts out of agreement with the first.
    /// </para>
    /// <para>
    /// A Jira key the task carries is forced onto the front of whichever title wins, once
    /// (<see cref="WithExternalKey"/>). The daemon knows the key with certainty, every repo rule
    /// that says anything about a title says the key belongs in it, and an agent that forgot it is
    /// cheaper to correct here than to catch on review. A GitHub issue number is never prefixed:
    /// the body's work-item line already cross-references it, and a bare <c>#42</c> in a squash
    /// subject is an issue link the platform never meant to make.
    /// </para>
    /// </summary>
    public static string Title(TaskDetails task, PrSummaryParser.PrSummary? prSummary)
    {
        string? key = JiraKey(task.ExternalReference);
        string? authored = prSummary?.Title is { } written && OneLine(written) is { Length: > 0 } folded
            ? folded
            : null;

        // An authored title is used at whatever length it was written, because a session that
        // composed one was told to write a title; only the fallback, which is an outcome sentence
        // by rule (PLAN.md §4) and routinely paragraph-length, is cut to a subject's size.
        return authored is not null
            ? CutAtWordBoundary(WithExternalKey(authored, key), MaxTitleLength)
            : CutAtWordBoundary(WithExternalKey(OneLine(task.Objective), key), SubjectTitleLength);
    }

    /// <summary>
    /// How long a fallback title is allowed to be. 72 because the title is a squash-merge commit
    /// subject by the time it matters, and 72 is the width every git tool in the chain formats one
    /// to.
    /// </summary>
    private const int SubjectTitleLength = 72;

    /// <summary>
    /// The outer bound on any title, authored or not. This is Hall9k's own bound and is
    /// deliberately not called GitHub's: the commonly quoted 256 is the documented limit on an
    /// <em>issue</em> title and demonstrably does not apply to a pull request's, since
    /// arx-platform PR #2042 opened on 2026-09-09 with a 334-character title (recoverable from its
    /// own rename event, which is how this task verified it rather than assuming). GitHub
    /// publishes no pull-request title limit at all, so a bound named after theirs would be a
    /// guess dressed as a fact (AGENTS.md: never guess at unobserved facts). 1024 is chosen to be
    /// safe under uncertainty in the direction that costs least: cutting earlier than GitHub would
    /// have costs a few words off a pathological title, while cutting later than GitHub allows
    /// fails <c>gh pr create</c> outright and with it the whole closeout.
    /// </summary>
    private const int MaxTitleLength = 1024;

    /// <summary>
    /// The title with the external key on the front, exactly once. Idempotent by inspection rather
    /// than by rule: a session that already wrote <c>ARX-4861: …</c>, as its repo's own rule tells
    /// it to, keeps its own wording untouched.
    /// <para>
    /// The inspection is a whole-token one, not a bare <c>StartsWith</c>: keys share prefixes with
    /// each other, so a title opening on a DIFFERENT card whose key merely extends this task's —
    /// <c>ARX-4861 compatibility …</c> under a task carrying <c>ARX-486</c> — reads as the key
    /// already being present and opens the pull request naming only the other card, which is the
    /// one shape the forced prefix exists to correct (independent pre-PR review, cycle 1, both
    /// lenses). Anything that is not a letter or a digit ends the token, so the ordinary
    /// <c>ARX-486:</c>, <c>ARX-486 </c> and the bare key alone all still count as written.
    /// </para>
    /// </summary>
    private static string WithExternalKey(string title, string? key) =>
        key is null || AlreadyKeyed(title, key) ? title : $"{key}: {title}";

    private static bool AlreadyKeyed(string title, string key) =>
        title.StartsWith(key, StringComparison.OrdinalIgnoreCase)
        && (title.Length == key.Length || !char.IsLetterOrDigit(title[key.Length]));

    /// <summary>
    /// The Jira key this task's reference carries, or null for every other kind of reference and
    /// for no reference at all. <see cref="ExternalReference.Key"/> answers for both providers;
    /// only Jira's answer is a token people say out loud and put at the front of a title.
    /// </summary>
    private static string? JiraKey(string? externalReference)
    {
        if (externalReference.IsBlank())
        {
            return null;
        }

        ExternalReference reference = ExternalReference.Parse(externalReference);
        return reference.Provider == WorkItemProvider.Jira && reference.Key.IsNotBlank() ? reference.Key : null;
    }

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="maxLength"/> at the last word boundary that
    /// fits, with an ellipsis saying so. Cut rather than rejected: a title too long is still the
    /// best description of this change anybody has, and a run that fails at <c>gh pr create</c>
    /// over one strands finished, gated work.
    /// <para>
    /// Only the word boundary is this method's own rule. Where no space is available the cut is
    /// <see cref="RelayedText.CutLength"/>'s, which lands on a text-element boundary rather than a
    /// raw char index — a title in a script that writes no spaces at all carries no space to
    /// prefer, so the fallback is the ordinary path rather than the exotic one for those, and a
    /// raw slice through a surrogate pair reaches <c>gh pr create --title</c>, and on a squash
    /// merge the default branch's own commit subject, as U+FFFD (independent pre-PR review, cycle
    /// 1, adversarial lens). <see cref="RelayedText.Printable"/>, which every relayed segment
    /// passes through, cannot save this one: it runs BEFORE the cut, and it is the cut that makes
    /// the half character.
    /// </para>
    /// </summary>
    private static string CutAtWordBoundary(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // One character of the budget belongs to the ellipsis, so the result is never itself over
        // the bound this was called to enforce.
        int room = maxLength - 1;
        // A space is never half of anything, so a cut at one needs no boundary check of its own.
        int boundary = text.LastIndexOf(' ', room);
        int cut = boundary > 0 ? boundary : RelayedText.CutLength(text, room);
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// Relayed text that belongs on one line — the objective, a checklist item — defused twice
    /// over: the closing keywords lose their power over the issue tracker, and the layout
    /// characters become spaces while everything the terminal or the renderer would obey rather
    /// than show is dropped. Words on separate lines do not run together, and a criterion cannot
    /// break out of the checklist item it is inside.
    /// </summary>
    private static string OneLine(string text) =>
        WithoutClosingKeywords(RelayedText.OneLine(text).Trim());

    /// <summary>
    /// Relayed text that is a block of prose and keeps its shape: the same defusal, but the line
    /// breaks and tabs survive, because they are this text's paragraphs and its lists.
    /// </summary>
    private static string Block(string text) => WithoutClosingKeywords(RelayedText.Printable(text));

    /// <summary>
    /// How much of the session's own prose the pull request carries. Generous on purpose: unlike
    /// <see cref="HandoffParser.MaxEventLength"/>, which bounds text travelling on a stream as a
    /// milestone, this is prose a reviewer reads on GitHub, so the bound exists only to stop a
    /// runaway paste and sits well under GitHub's own 65,536-character body limit.
    /// </summary>
    private const int MaxRelayedBodyLength = 30_000;

    /// <summary>
    /// The authored body, bounded and then defused, with an honest note when the bound actually
    /// bit — the shape <see cref="HandoffParser.BoundForEvent"/> already uses, since a silently
    /// clipped body reads exactly like a complete one (the AGENTS.md never-guess rule, applied to
    /// omission).
    /// <para>
    /// Bounded BEFORE <see cref="Block"/> rather than after, which is not merely tidier: the
    /// defusal works by inserting a backtick pair around each reference it neutralises, so cutting
    /// defused text at a fixed offset can land between an inserted pair and leave the reference it
    /// had just closed bare and autolinked again, putting a cross-reference on an unrelated issue
    /// from a pull request that has nothing to do with it. Cutting first means every keyword the
    /// defusal sees is whole. The cut itself lands on a line boundary where one is available, so
    /// the note below reads as its own paragraph instead of as the tail of a half sentence.
    /// </para>
    /// <para>
    /// The note names no path: a run directory is a daemon-machine-local absolute path that stops
    /// resolving the moment the render sweep archives the task, and publishing it would put the
    /// operator's home directory, and so their username, on a public repository.
    /// <c>h9k task show</c> is the durable pointer, and the file's own name is enough to find it
    /// beside the run's other artifacts. This is the one pointer the 2026-09-19 ruling leaves in
    /// place, because it is not an addition around the prose: it is the honest statement that the
    /// prose above it is incomplete, which a body that silently dropped the rest would not make.
    /// </para>
    /// </summary>
    private static string BoundedBlock(string authored, Guid taskId)
    {
        if (authored.Length <= MaxRelayedBodyLength)
        {
            return Block(authored);
        }

        // A line feed, like a space, is never half of anything; without one the cut is
        // RelayedText.CutLength's, for the reason CutAtWordBoundary spells out — a raw slice at
        // MaxRelayedBodyLength can leave a lone surrogate, and Printable keeps one (it is neither
        // a control character nor a layout override) all the way onto the pull request.
        int lineBreak = authored.LastIndexOf('\n', MaxRelayedBodyLength - 1);
        int cut = lineBreak > 0 ? lineBreak : RelayedText.CutLength(authored, MaxRelayedBodyLength);
        string kept = authored[..cut].TrimEnd();
        return $"{Block(kept)}\n\n[Truncated at {MaxRelayedBodyLength} characters. The whole summary is "
            + $"in this run's `pr-summary.md`; see `h9k task show {taskId}`.]";
    }

    /// <summary>
    /// The authored body without a work-item line of its own on the front. The skill tells the
    /// session to leave that line out, since the platform writes it, and this dedupes anyway: two
    /// work-item lines on one pull request is the shape a reviewer reads as a mistake, and the
    /// agent's own copy is the one that can name the wrong item.
    /// </summary>
    private static string WithoutLeadingWorkItemLine(string authored)
    {
        string[] lines = authored.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].IsBlank())
            {
                continue;
            }

            return lines[index].TrimStart().StartsWith(WorkItemLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Join('\n', lines[(index + 1)..]).Trim()
                : authored;
        }

        return authored;
    }

    /// <summary>The label both the platform's own work-item line and the dedupe above are keyed on.</summary>
    private const string WorkItemLabel = "Work item:";

    /// <summary>
    /// The whole body, composed as a pure function of what the run and the task already hold, so
    /// it can be exercised without <c>gh</c> and without a run directory.
    /// <para>
    /// <paramref name="prSummary"/> is the pull request the build session composed for itself
    /// (<c>pr-summary.md</c>, via <see cref="PrSummaryArtifact"/>). When it carries a body, the
    /// whole pull request is the work-item line and that prose, and nothing else: the platform
    /// adds one line a reviewer clicks and then gets out of the way (Brian's ruling, 2026-09-19).
    /// </para>
    /// <para>
    /// What used to follow the prose is gone for good rather than made configurable: the collapsed
    /// acceptance-criteria block, the <c>Left unfixed</c> and <c>Review ride-alongs</c> notes, the
    /// reduced-review note, and the run footer. The criteria live on the card, the review
    /// residuals live in the run record (<c>review-N-findings.md</c>, and
    /// <c>h9k task show</c> reads the same tally back from the run's own stream), and the
    /// provenance is the run record too. Origin incident: bioage-calculator pull request #4
    /// (ARX-5817, task 4442dd3a) opened on 2026-09-18 with a 3,837-byte, 36-line body for a
    /// 56-line file, of which roughly a third was these additions and the rest read like a
    /// transcript.
    /// </para>
    /// <para>
    /// <paramref name="agentSummary"/> is the session's closing narration, and it is relayed only
    /// when no authored body exists. The two are alternatives, never a stack: a session that
    /// composed a pull request said what it wanted a reviewer to know there, and repeating its run
    /// narration underneath is exactly the transcript the skill's own "Keep out" list forbids.
    /// </para>
    /// </summary>
    public static string Build(
        RunDetails run, TaskDetails task, string? agentSummary, Uri? sourceUrl,
        PrSummaryParser.PrSummary? prSummary = null)
    {
        // Blankness is judged AFTER the work-item line is stripped, not before: a session whose
        // whole body was the one line the platform writes for it has, in fact, composed nothing,
        // and treating that as an authored body would open the pull request on an empty prose
        // section with the run narration suppressed underneath it.
        string? stripped = prSummary?.Body is { } written ? WithoutLeadingWorkItemLine(written) : null;
        string? authored = stripped.IsNotBlank() ? stripped : null;
        string? mention = SourceMention(task.ExternalReference, sourceUrl);
        StringBuilder body = new();

        if (authored is not null)
        {
            if (mention is not null)
            {
                body.AppendLine(mention);
                body.AppendLine();
            }

            body.AppendLine(BoundedBlock(authored, run.TaskId));
            return body.ToString();
        }

        body.AppendLine(OneLine(task.Objective));
        body.AppendLine();
        body.AppendLine("## Acceptance criteria");
        foreach (string criterion in task.AcceptanceCriteria)
        {
            body.AppendLine($"- [ ] {OneLine(criterion)}");
        }

        if (mention is not null)
        {
            body.AppendLine();
            body.AppendLine(mention);
        }

        if (agentSummary.IsNotBlank())
        {
            body.AppendLine();
            body.AppendLine("## Agent summary");
            body.AppendLine(Block(agentSummary));
        }

        return body.ToString();
    }

    /// <summary>
    /// The line that links the work back to the item it belongs to, and, since the 2026-09-19
    /// ruling, the only thing the platform writes around a session's own prose. It is a mention of
    /// that item's URL, which GitHub turns into a cross-reference on the issue's own timeline.
    /// <para>
    /// The link text is the key people actually say out loud rather than the URL itself:
    /// <c>[ARX-5817](https://…/browse/ARX-5817)</c> for a Jira card, <c>[#123](…/issues/123)</c>
    /// for a GitHub issue. A reviewer scanning the top of a pull request reads the card number,
    /// not the host and path it happens to live under, and one short line is what makes the
    /// platform's single addition sit above the prose without competing with it.
    /// </para>
    /// <para>
    /// Deliberately a mention and not a closing keyword. "Closes #42" would make merging this
    /// pull request change the issue's state, and Hall9k does not move an external item's status:
    /// which transitions should follow a merge is a policy question (SLICE-1 S1-11, Decisions Log
    /// #65, where Jira gets a comment at merge and never a transition, for the same reason). A
    /// cross-reference gives a reviewer the round trip without the platform deciding anything.
    /// A <c>#123</c> that is a markdown link's own text is not a second reference either: it is
    /// already a link, so GitHub renders it and leaves it alone.
    /// </para>
    /// <para>
    /// The wording says what is true of both ways a task acquires a reference, and says no more
    /// than that. "Adopted from" was true while adoption was the only route (§3.1a), and is a
    /// false provenance claim for a card that exists <em>because</em> of the task
    /// (h9k task push-to-jira). The projection carries one reference field either way, so the
    /// body names the link rather than guessing which direction it was made in.
    /// </para>
    /// <para>
    /// The URL is resolved by the caller through the connection-aware resolver seam rather than
    /// formatted here, because placing a Jira reference needs the site its connection recorded
    /// and this class has no session to read one from. A reference no registered source can place
    /// falls back to its canonical form, unlinked, which is still the honest identifier: a link
    /// text with no link behind it would read as a URL the platform simply failed to print.
    /// </para>
    /// </summary>
    private static string? SourceMention(string? externalReference, Uri? sourceUrl)
    {
        if (externalReference.IsBlank())
        {
            return null;
        }

        ExternalReference reference = ExternalReference.Parse(externalReference);
        return sourceUrl is null
            ? $"{WorkItemLabel} {reference}"
            : $"{WorkItemLabel} [{LinkText(reference)}]({sourceUrl})";
    }

    /// <summary>
    /// What the work-item link is called: the Jira key as written, and a GitHub issue's bare
    /// number with the <c>#</c> people say it with, since <see cref="ExternalReference.Key"/>
    /// deliberately drops the repository that minted it. Any other provider gets the key it
    /// carries, unadorned, rather than a form invented for it.
    /// </summary>
    private static string LinkText(ExternalReference reference) =>
        reference.Provider == WorkItemProvider.GitHub ? $"#{reference.Key}" : reference.Key;

    /// <summary>
    /// A closing keyword rendered so GitHub reads it as words rather than as an instruction. The
    /// mention above is the only thing in this body allowed to reach the issue tracker, and only
    /// as a cross-reference; nothing Hall9k merely relays may move an item's state.
    /// <para>
    /// The rule itself is <see cref="RelayedText.WithoutClosingKeywords"/>, beside the seam that
    /// lets such text in, because the objective now arrives here already defused: the CLI applies
    /// the same rule when it seeds an objective from an issue title, so the keyword is dead before
    /// an agent ever reads it and cannot be echoed live into a commit subject. Two surfaces
    /// answering one question from two hand-written regexes are two answers that drift, and the
    /// shared one is what makes the second pass here idempotent — a reference already inside a
    /// code span is left alone rather than wrapped twice.
    /// </para>
    /// <para>
    /// This is only half of what relayed text needs, which is why nothing calls it directly: the
    /// body is not merely read on github.com. A repository set to squash with "title and
    /// description" puts the whole of it into the commit message, so an escape sequence or a
    /// bidirectional override in it lands in the repository's history and in every terminal that
    /// later runs git log — the exact threat <see cref="Title"/> was hardened against, arriving
    /// through the paragraph underneath it. So every relayed segment goes through
    /// <see cref="OneLine"/> or <see cref="Block"/>, which pair this with the printable rule.
    /// </para>
    /// </summary>
    private static string WithoutClosingKeywords(string text) => RelayedText.WithoutClosingKeywords(text);
}
