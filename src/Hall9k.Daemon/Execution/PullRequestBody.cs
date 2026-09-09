using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Run;
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
    /// The note names no path, for the reason <see cref="RideAlongNote"/> spells out at length: a
    /// run directory is a daemon-machine-local absolute path that stops resolving the moment the
    /// render sweep archives the task, and publishing it would put the operator's home directory,
    /// and so their username, on a public repository. <c>h9k task show</c> is the durable pointer,
    /// and the file's own name is enough to find it beside the run's other artifacts.
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
    /// Relayed text shown as an inline code span, fenced by a backtick run the text itself cannot
    /// close: <see cref="RelayedText.FenceFor"/>'s rule, applied inline the way
    /// <c>SweepDraftTask</c> already applies it, with the CommonMark padding space on both sides
    /// so the span still closes when the text begins or ends with a backtick of its own.
    /// <para>
    /// A single hard-coded backtick pair is the obvious wrapper and is wrong here, which is the
    /// defect this method exists to prevent (independent pre-PR review, cycle 5, adversarial
    /// lens). <see cref="OneLine"/> has already run <see cref="RelayedText.WithoutClosingKeywords"/>
    /// over this text, and that defusal works by <em>inserting</em> a backtick pair on both sides of
    /// the reference it neutralises — so a location reading <c>src/Foo.cs:12 closes #500</c> arrives
    /// here already carrying an inserted pair of backticks around <c>#500</c>. Wrapping that in one
    /// more hard-coded single-backtick pair still breaks out: a single backtick pairs with the
    /// nearest single-backtick run rather than the whole inserted pair, so the wrapper's opener
    /// closes against the first inserted backtick instead of surviving to the wrapper's own closer.
    /// That leaves <c>#500</c> bare in the rendered body and puts a cross-reference on an unrelated
    /// issue's timeline from a pull request that has nothing to do with it — precisely the leak the
    /// defusal had just closed. A location that carries interior backticks of its own breaks out the
    /// same way, and takes whatever markdown follows with it.
    /// </para>
    /// </summary>
    private static string InlineCode(string text)
    {
        string fence = RelayedText.FenceFor(text);
        return $"{fence} {text} {fence}";
    }

    /// <summary>
    /// The whole body, composed as a pure function of what the run and the task already hold, so
    /// it can be exercised without <c>gh</c> and without a run directory.
    /// <para>
    /// <paramref name="prSummary"/> is the pull request the build session composed for itself
    /// (<c>pr-summary.md</c>, via <see cref="PrSummaryArtifact"/>). When it carries a body, that
    /// prose is what a reviewer reads, verbatim between the platform's own bookkeeping: the
    /// work-item line above it, and the acceptance criteria, the review residuals and the run
    /// footer below. The criteria stay on the page but move into a collapsed details block: the
    /// body is the one artifact of a run that outlives Hall9k, and the reviewer of an
    /// agent-produced change is checking it against a contract, but that contract is not the first
    /// thing they should have to scroll past (Brian's ruling, 2026-09-09).
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
            if (task.AcceptanceCriteria.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("<details><summary>Acceptance criteria</summary>");
                body.AppendLine();
                foreach (string criterion in task.AcceptanceCriteria)
                {
                    body.AppendLine($"- [ ] {OneLine(criterion)}");
                }

                body.AppendLine();
                body.AppendLine("</details>");
            }
        }
        else
        {
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
        }

        if (run.ReviewResidualsUnfixed > 0)
        {
            body.AppendLine();
            body.AppendLine(UnfixedNote(run));
        }

        if (run.ReviewResidualsRideAlong > 0)
        {
            body.AppendLine();
            body.AppendLine(RideAlongNote(run));
        }

        if (run.ReviewStageComposition != ReviewStageComposition.FullPipeline)
        {
            body.AppendLine();
            body.AppendLine(ReducedReviewNote(run.ReviewStageComposition));
        }

        long totalTokens = run.InputTokens + run.CacheReadInputTokens + run.CacheCreationInputTokens + run.OutputTokens;
        body.AppendLine();
        body.AppendLine("---");
        body.AppendLine($"Hall9k run `{run.Id}` · {totalTokens} tokens");
        return body.ToString();
    }

    /// <summary>
    /// What the run left unfixed, unlike a ride-along, because the platform had already decided it
    /// met the fix bar — an in-scope medium or high — and the loop simply ran out before a fix
    /// session ever read it (Decisions Log #87, adversarial review, the routed finding that opened
    /// this task: the shape it exists to name is a human resolving a capped park with
    /// <c>h9k review resolve --merge-ready</c>, so this is the one line on the pull request itself
    /// saying so, rather than a settled line that reads as though only polish was left behind).
    /// Named the same way <see cref="RideAlongNote"/> names its own tally, for the same reason.
    /// </summary>
    private static string UnfixedNote(RunDetails run)
    {
        string plural = run.ReviewResidualsUnfixed == 1 ? "finding" : "findings";
        string header = $"**Left unfixed:** {run.ReviewResidualsUnfixed} {plural} the platform decided met the fix "
            + "bar, but no fix session reached them before this review loop ended";
        if (run.ReviewUnfixedFindings.Count == 0)
        {
            return $"{header} — see `h9k task show {run.TaskId}` for the review history.";
        }

        StringBuilder note = new();
        note.AppendLine($"{header}:");
        foreach (ReviewUnfixedFinding finding in run.ReviewUnfixedFindings)
        {
            string severity = finding.Severity == ReviewSeverity.Unknown ? "ungraded" : finding.Severity.Value.ToLowerInvariant();
            string location = finding.Location.IsBlank() ? "no location stated" : InlineCode(OneLine(finding.Location));
            note.AppendLine($"- {severity} — {location}");
        }

        note.AppendLine();
        note.Append($"See `h9k task show {run.TaskId}` for the full review history.");
        return note.ToString();
    }

    /// <summary>
    /// What the run left riding along rather than fixed (Decisions Log #87, and the
    /// FinalFullPass-only narrowing task: a mandatory FinalFullPass records merge-ready when
    /// every finding it attaches is below High). Nothing about a ride-along is on this pull
    /// request's diff — it is what the review loop declined to spend a cycle on — so a reviewer
    /// reading only the diff would never learn it exists without this line naming a way to find
    /// it. <see cref="RunDetails.ReviewResidualsRideAlong"/> is a run-lifetime tally
    /// (<c>RunAggregate.DeriveResidualTally</c> sums every cycle's own ride-alongs, deduplicated
    /// against what an earlier, normally-concluded track already recorded), so it can count
    /// residuals a single cycle's own findings file never held — pointing at one specific
    /// `review-&lt;cycle&gt;-findings.md` would name a file that does not contain everything the
    /// count refers to. It would also be a daemon-machine-local absolute path: the one artifact
    /// this class exists to write outlives the run directory (reviewers read it on GitHub long
    /// after), and the render sweep moves this very task under `tasks/_archive/` the moment it
    /// merges, so a path resolved at PR-open time stops resolving for every reader from that
    /// point on — and it would publish the operator's home-directory path, and thus their
    /// username, to a public repository. <c>h9k task show</c> is the durable pointer instead: it
    /// answers from the task's own event stream regardless of archive state or which machine
    /// runs it.
    /// <para>
    /// The count alone used to be the whole line (independent pre-PR review, cycle 2, conformance
    /// finding: "the owner still has no way to see, or even identify, the findings the final pass
    /// carried"), with `h9k task show` naming only the same count back — a circular pointer that
    /// never actually surfaced a severity or a location. <see cref="RunDetails.ReviewRideAlongFindings"/>
    /// is what closes that: each entry's own grade and location, named inline here rather than
    /// requiring a second command just to learn what the first one already tallied. A run settled
    /// before that field existed still has only the count, and says so honestly rather than
    /// pretending the detail was always there.
    /// </para>
    /// </summary>
    private static string RideAlongNote(RunDetails run)
    {
        string plural = run.ReviewResidualsRideAlong == 1 ? "finding" : "findings";
        string header = $"Review ride-alongs: {run.ReviewResidualsRideAlong} {plural} below the fix bar of "
            + "whichever cycle recorded them, carried rather than spending another review cycle on";
        if (run.ReviewRideAlongFindings.Count == 0)
        {
            return $"{header} — see `h9k task show {run.TaskId}` for the review history.";
        }

        StringBuilder note = new();
        note.AppendLine($"{header}:");
        foreach (ReviewRideAlongFinding finding in run.ReviewRideAlongFindings)
        {
            string severity = finding.Severity == ReviewSeverity.Unknown ? "ungraded" : finding.Severity.Value.ToLowerInvariant();
            string location = finding.Location.IsBlank() ? "no location stated" : InlineCode(OneLine(finding.Location));
            note.AppendLine($"- {severity} — {location}");
        }

        note.AppendLine();
        note.Append($"See `h9k task show {run.TaskId}` for the full review history.");
        return note.ToString();
    }

    /// <summary>
    /// The one line stating that this run's pre-PR review was reduced or skipped outright
    /// (task: the review pipeline's stage composition becomes configuration recorded per run),
    /// so the human doing the merge has the signal on the page where the merge decision actually
    /// happens rather than only on `h9k task show`'s Stages column (independent pre-PR review,
    /// cycle 1, conformance finding: a composition-`none` settle reads exactly like a clean
    /// full-pipeline one here, with nothing saying no reviewer ever read the diff).
    /// </summary>
    private static string ReducedReviewNote(ReviewStageComposition composition) =>
        $"**Review stage composition:** `{composition.Value}` — this run's pre-PR review was reduced "
        + "from the full pipeline; see `h9k task show` for what that means.";

    /// <summary>
    /// The line that links the work back to the item it belongs to: a plain mention of that
    /// item's URL, which GitHub turns into a cross-reference on the issue's own timeline.
    /// <para>
    /// Deliberately a mention and not a closing keyword. "Closes #42" would make merging this
    /// pull request change the issue's state, and Hall9k does not move an external item's status:
    /// which transitions should follow a merge is a policy question (SLICE-1 S1-11, Decisions Log
    /// #65, where Jira gets a comment at merge and never a transition, for the same reason). A
    /// cross-reference gives a reviewer the round trip without the platform deciding anything.
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
    /// falls back to its canonical form, which is still the honest identifier.
    /// </para>
    /// </summary>
    private static string? SourceMention(string? externalReference, Uri? sourceUrl) =>
        externalReference.IsBlank()
            ? null
            : $"{WorkItemLabel} {sourceUrl?.ToString() ?? ExternalReference.Parse(externalReference).ToString()}";

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
