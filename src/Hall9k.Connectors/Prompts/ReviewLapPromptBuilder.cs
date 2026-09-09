using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// What this node can read of the AUTHOR's own run for the pull request under review — the
/// platform's own account of what its review loop did, not a judgment about the diff. Present
/// only when the author's task lives in this same store (a single-node install, or a shared
/// database); absent is the ordinary case on a genuinely foreign pull request and says so.
/// <para>
/// <see cref="UnclaimedResiduals"/> is the pointer the briefing leads with, per the 2026-09-06
/// ruling behind Decisions Log #149: it is the only part of this account produced by a hunt with
/// no knowledge of the author's intent, so it points a reviewer at real, un-vouched-for ground
/// without steering them the way a curated "look here" list would.
/// </para>
/// </summary>
public sealed record ReviewLapAuthorRun(
    string Settlement,
    int ResidualsFixed,
    int ResidualsRouted,
    IReadOnlyList<string> UnclaimedResiduals,
    IReadOnlyList<string> Rulings);

/// <summary>
/// One of the reviewer's own review threads, with only what arrived on it since their review
/// (task: a pr-review task stays open while the pull request's review threads are unresolved).
/// <para>
/// <see cref="NewComments"/> is verbatim, because paraphrasing the author's answer is exactly the
/// thing a scoped lap must not do: the reviewer is reading a reply, and a summary of a reply is a
/// different artefact. An empty list on a thread that IS listed means the thread's own state
/// changed without a comment — resolved, most often — which is news of its own.
/// </para>
/// </summary>
/// <param name="Location">Where the thread sits, as a reviewer names one: <c>path:line</c>.</param>
/// <param name="IsResolved">Whether it is resolved right now.</param>
/// <param name="NewComments">Everything said in it after the reviewer's own last word, verbatim.</param>
/// <param name="UnreadCommentCount">
/// How many comments on this thread the provider's own per-thread page cap left unread, and so
/// absent from <paramref name="NewComments"/> — always the newest ones, which on a scoped lap are
/// the very comments it was opened to read. Zero for every thread inside the cap. Carried rather
/// than dropped because a packet that showed the comments it happened to have and said nothing
/// about the rest reads exactly like a complete one.
/// </param>
public sealed record ScopedReviewThreadDelta(
    string Location, bool IsResolved, IReadOnlyList<string> NewComments, int UnreadCommentCount = 0);

/// <summary>
/// The whole packet a scoped lap reads (<c>h9k pr review --since-my-review</c>): the thread
/// deltas since the reviewer's last review, and the commits pushed since it. Nothing else — no
/// objective, no blast radius, no CI, no earlier findings report — because the reviewer has
/// already read all of that once, and re-reading a docs pull request end to end for five reply
/// threads is exactly the waste this flag exists to avoid (the origin gap, 2026-09-08: the reply
/// analysis was run as a read-only side agent outside hall9k because <c>--from-pr</c> had no way
/// to scope it).
/// <para>
/// Every "could not read" here is carried as a stated absence with the command that answers it,
/// never as silence: a diff the range could not resolve (a force-push that dropped the reviewed
/// commit) is a fact the reviewer needs, and a packet that simply omitted it would read as
/// "nothing changed in the code".
/// </para>
/// </summary>
/// <param name="ReviewerLogin">The login whose threads and review this packet is scoped to, read back from gh.</param>
/// <param name="ReviewedHeadSha">The head the reviewer's own review was posted against, or null when none was recorded.</param>
/// <param name="CurrentHeadSha">The head right now.</param>
/// <param name="Threads">The reviewer's threads that moved. Empty when none did.</param>
/// <param name="UnchangedThreadCount">How many of the reviewer's threads did not move, so the packet's scope is stated rather than implied.</param>
/// <param name="NewCommits">One line per commit pushed since the review, oldest first. Empty when none were.</param>
/// <param name="Diff">The diff of those commits, or null when the range could not be read.</param>
/// <param name="DiffNote">Why the diff is absent or shortened, or null when it is neither.</param>
/// <param name="ThreadPageTruncated">
/// Whether the provider's own thread page was capped, which makes <paramref name="Threads"/> and
/// <paramref name="UnchangedThreadCount"/> a floor rather than a count: threads of the reviewer's
/// own may be missing from this packet entirely. Stated in the briefing for the same reason every
/// other short read here is — silence would read as "these are all your threads" (independent
/// pre-PR review, cycle 2).
/// </param>
/// <param name="ReReviewRequested">
/// Whether a review request stands against <paramref name="ReviewerLogin"/> right now — the author
/// has asked them back. Carried because it is the one thing that can summon a scoped lap with
/// nothing in either half of the packet: an author who resolves the reviewer's threads themselves
/// and re-requests the review, with no reply and no push, leaves a packet that shows nothing and a
/// reviewer who was nonetheless asked to look again (independent pre-PR review, cycle 1,
/// adversarial lens). Stated rather than inferred, for the same reason every other short read here
/// is stated.
/// </param>
public sealed record ScopedReviewPacket(
    string ReviewerLogin,
    string? ReviewedHeadSha,
    string? CurrentHeadSha,
    IReadOnlyList<ScopedReviewThreadDelta> Threads,
    int UnchangedThreadCount,
    IReadOnlyList<string> NewCommits,
    string? Diff,
    string? DiffNote,
    bool ThreadPageTruncated = false,
    bool ReReviewRequested = false);

/// <summary>
/// Everything the opening briefing states, gathered by the caller so the composition itself is
/// pure and testable. Every field is either an observed fact or an admitted absence — there is
/// no field here the builder is allowed to fill in.
/// </summary>
/// <param name="TaskId">The pr-review task the lap is attached to.</param>
/// <param name="PullRequest">The pull request as gh reported it moments ago.</param>
/// <param name="StatedObjective">
/// The objective as the AUTHOR stated it, from their own task when this node can read it —
/// null when it cannot, in which case the pull request's own title and body are all there is
/// and the briefing says which it is showing.
/// </param>
/// <param name="AcceptanceCriteria">The author's own acceptance criteria; empty when unreadable here.</param>
/// <param name="AuthorTaskShortId">The author's task id, so the reviewer can go read it themselves.</param>
/// <param name="WorktreePath">The read-only checkout, or empty when <c>--no-worktree</c> skipped it.</param>
/// <param name="FindingsReport">
/// The pr-review task's own merged findings report, verbatim, when its automated run has already
/// parked one. Null when no automated pass has produced one yet — the reviewer got here first,
/// which is a legitimate way to run a lap and not a missing prerequisite.
/// </param>
/// <param name="AuthorRun">See <see cref="ReviewLapAuthorRun"/>; null when this node cannot read the author's run.</param>
/// <param name="SinceMyReview">
/// The scoped packet (<c>h9k pr review --since-my-review</c>), or null for an ordinary lap. When
/// it is present the briefing is composed from it INSTEAD of the objective, blast-radius, checks,
/// findings-report and author-run sections — see <see cref="ScopedReviewPacket"/> for why.
/// </param>
public sealed record ReviewLapBriefing(
    Guid TaskId,
    PullRequestSurface PullRequest,
    string ProjectName,
    string RepositoryPath,
    string WorktreePath,
    string? StatedObjective,
    IReadOnlyList<string> AcceptanceCriteria,
    string? AuthorTaskShortId,
    string? FindingsReport,
    ReviewLapAuthorRun? AuthorRun,
    ScopedReviewPacket? SinceMyReview = null);

/// <summary>
/// The opening briefing a reviewer's own review lap starts with (<c>h9k pr review</c>, Decisions
/// Log #149) — deliberately its own builder rather than a mode of
/// <see cref="WorkPromptBuilder"/>, which exists to hand a session a task to BUILD: every rule
/// in there is about a diff the session owns, a branch it commits to, and a pull request the
/// platform will open, and a review lap has none of those.
/// <para>
/// <b>Factual and unprescriptive, by ruling.</b> The briefing states what is there — the stated
/// objective, the surfaces touched, what CI ran, what the machines already found — and then
/// stops. It offers no test scenarios, no "areas of concern", no suggested review order: those
/// are the reviewer's own to form, and a briefing that supplies them replaces the reviewer's
/// judgment with the platform's while looking like it is helping. The session offers them
/// readily the moment the reviewer asks; what it must not do is volunteer them first.
/// </para>
/// </summary>
public static class ReviewLapPromptBuilder
{
    public static string Build(ReviewLapBriefing briefing)
    {
        StringBuilder prompt = new();
        PullRequestSurface pullRequest = briefing.PullRequest;

        prompt.AppendLine("# Review lap");
        prompt.AppendLine();
        prompt.AppendLine(
            $"You are helping a human reviewer review pull request {pullRequest.Repository}#{pullRequest.Number}"
            + (pullRequest.AuthorLogin.IsNotBlank() ? $", opened by {OneLine(pullRequest.AuthorLogin)}" : string.Empty)
            + $": {OneLine(pullRequest.Title)}");
        if (pullRequest.Url is not null)
        {
            prompt.AppendLine();
            prompt.AppendLine(pullRequest.Url.ToString());
        }

        prompt.AppendLine();
        prompt.AppendLine(
            $"It targets `{pullRequest.BaseRefName}` from `{pullRequest.HeadRefName}`, and its head right now is "
            + $"`{ShortSha(pullRequest.HeadSha)}`. It is not yours: nothing you do in this lap writes to it.");
        prompt.AppendLine();

        if (briefing.SinceMyReview is { } scoped)
        {
            // The five sections below are deliberately skipped whole, not trimmed: the reviewer
            // has read the objective, the blast radius, the checks and the machines' findings once
            // already, and re-stating them is what makes a second full pass out of a question
            // about five replies. What replaces them is the packet and nothing else.
            AppendSinceMyReviewSection(prompt, briefing, scoped);
        }
        else
        {
            AppendObjectiveSection(prompt, briefing);
            AppendBlastRadiusSection(prompt, pullRequest);
            AppendChecksSection(prompt, pullRequest);
            AppendFindingsReportSection(prompt, briefing);
            AppendAuthorRunSection(prompt, briefing.AuthorRun);
        }

        AppendWorkingArrangementSection(prompt, briefing);
        AppendRulesSection(prompt, briefing);
        AppendClosingSection(prompt, briefing);

        return prompt.ToString();
    }

    /// <summary>
    /// What the change is FOR, in the author's own words when this node can read them. The
    /// distinction between "the author stated this" and "this is the pull request's own title and
    /// body" is stated rather than smoothed over: a reviewer weighing a diff against its intent
    /// needs to know whether they are reading the intent or a description of the change.
    /// </summary>
    private static void AppendObjectiveSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        prompt.AppendLine("## Stated objective");
        prompt.AppendLine();
        if (briefing.StatedObjective.IsNotBlank())
        {
            prompt.AppendLine(
                "From the author's own task on this node"
                + (briefing.AuthorTaskShortId.IsNotBlank() ? $" ({briefing.AuthorTaskShortId})" : string.Empty)
                + ", not from the pull request's description:");
            prompt.AppendLine();
            prompt.AppendLine(Block(briefing.StatedObjective));
            if (briefing.AcceptanceCriteria.Count > 0)
            {
                prompt.AppendLine();
                prompt.AppendLine("Acceptance criteria, as the author's task records them:");
                prompt.AppendLine();
                foreach (string criterion in briefing.AcceptanceCriteria)
                {
                    prompt.AppendLine($"- {OneLine(criterion)}");
                }
            }
            else
            {
                prompt.AppendLine();
                prompt.AppendLine(
                    "The author's task records no acceptance criteria, so there is no contract here to check "
                    + "the diff against — only the objective above.");
            }
        }
        else
        {
            prompt.AppendLine(
                "This node cannot read an authoring task for this pull request, so there is no stated "
                + "objective or acceptance-criteria contract to check the diff against — only what the pull "
                + "request itself says about itself, which is the author's description of the change rather "
                + "than the intent behind it:");
            prompt.AppendLine();
            prompt.AppendLine(briefing.PullRequest.Body.IsNotBlank()
                ? Block(briefing.PullRequest.Body)
                : "(the pull request has no description)");
        }

        prompt.AppendLine();
    }

    /// <summary>
    /// The surfaces touched and how far the change reaches — counts and groupings only. What a
    /// blast radius is here is deliberately arithmetic: how many files, in which top-level
    /// surfaces, with how many lines each way. Nothing in it grades the change or nominates a
    /// file as risky, which is the reviewer's call and the thing this briefing is under a ruling
    /// not to pre-empt.
    /// </summary>
    private static void AppendBlastRadiusSection(StringBuilder prompt, PullRequestSurface pullRequest)
    {
        prompt.AppendLine("## Surfaces touched");
        prompt.AppendLine();
        if (pullRequest.Files.Count == 0)
        {
            // The count is stated only when there IS one. ChangedFiles falls back to the list's
            // own length, which is zero here, and "0 file(s)" in the branch whose whole point is
            // admitting a gap reads as "this pull request changes nothing" — the opposite of what
            // is known (self-review, round two).
            string counted = pullRequest.ChangedFiles > 0
                ? $"{pullRequest.ChangedFiles} file(s), +{pullRequest.Additions}/-{pullRequest.Deletions}"
                : $"+{pullRequest.Additions}/-{pullRequest.Deletions}, with no file count reported either";
            prompt.AppendLine(
                "gh reported no file list for this pull request, so the blast radius below could not be "
                + $"computed. GitHub's own totals for it are {counted}. Read the diff directly "
                + "(`git diff` in the worktree, if you have one) rather than treating this absence as a "
                + "small change.");
            prompt.AppendLine();
            return;
        }

        string fileWord = pullRequest.ChangedFiles == 1 ? "file" : "files";
        prompt.AppendLine(
            $"{pullRequest.ChangedFiles} {fileWord}, +{pullRequest.Additions}/-{pullRequest.Deletions} overall, "
            + $"across {DescribeSurfaces(pullRequest.Files)}:");
        // GitHub paginates the files list, so a very large pull request comes back with an honest
        // count and a short list. Said out loud rather than left to be inferred from a grouping
        // that silently covers part of the change (AGENTS.md, never guess at unobserved facts):
        // a reviewer who reads the groups below as the whole blast radius would be wrong, and
        // nothing else on this screen would tell them (self-review, round one).
        if (pullRequest.ChangedFiles > pullRequest.Files.Count)
        {
            prompt.AppendLine();
            prompt.AppendLine(
                $"GitHub served only {pullRequest.Files.Count} of those {pullRequest.ChangedFiles} files, so "
                + "the grouping below covers part of the change rather than all of it. The totals above are "
                + "the pull request's own and are complete; read the diff directly for the rest.");
        }

        prompt.AppendLine();
        foreach (IGrouping<string, PullRequestFileChange> surface in GroupBySurface(pullRequest.Files))
        {
            int additions = surface.Sum(file => file.Additions);
            int deletions = surface.Sum(file => file.Deletions);
            prompt.AppendLine($"- `{surface.Key}` — {surface.Count()} file(s), +{additions}/-{deletions}");
            foreach (PullRequestFileChange file in surface.OrderByDescending(file => file.Additions + file.Deletions))
            {
                prompt.AppendLine($"  - `{file.Path}` +{file.Additions}/-{file.Deletions}");
            }
        }

        prompt.AppendLine();
    }

    /// <summary>
    /// What CI ran, and the honest three-way distinction the rollup actually supports: checks
    /// that reported a conclusion, checks still running (which have concluded nothing), and no
    /// rollup observed at all. The last one is the one that matters most to get right — an empty
    /// section reads as "CI is green", and this repository's own gates are exactly the thing a
    /// reviewer would otherwise assume ran.
    /// </summary>
    private static void AppendChecksSection(StringBuilder prompt, PullRequestSurface pullRequest)
    {
        prompt.AppendLine("## What CI ran");
        prompt.AppendLine();
        if (!pullRequest.ChecksObserved)
        {
            prompt.AppendLine(
                "GitHub reported no status rollup for this head at all — which is not the same as "
                + "\"the checks passed\" and not the same as \"there are none\". Nothing about CI is known here.");
            prompt.AppendLine();
            return;
        }

        if (pullRequest.Checks.Count == 0)
        {
            prompt.AppendLine("GitHub reported a status rollup with no checks in it: nothing ran against this head.");
            prompt.AppendLine();
            return;
        }

        foreach (PullRequestCheck check in pullRequest.Checks)
        {
            string name = check.Workflow.IsNotBlank() ? $"{OneLine(check.Workflow)} / {OneLine(check.Name)}" : OneLine(check.Name);
            string outcome = check.Conclusion.IsNotBlank()
                ? OneLine(check.Conclusion)
                : $"{OneLine(check.Status)} — no conclusion yet";
            prompt.AppendLine($"- {name}: {outcome}");
        }

        prompt.AppendLine();
    }

    /// <summary>
    /// The scoped lap's whole briefing body (<c>h9k pr review --since-my-review</c>): what moved
    /// on the reviewer's own threads since their review, and what was pushed since it.
    /// <para>
    /// It states its own scope out loud — how many of the reviewer's threads it is NOT showing,
    /// and that the objective, blast radius, checks and earlier findings are deliberately absent
    /// — because a session handed a narrow packet with no note about its narrowness will read it
    /// as the whole picture and reason as though nothing else exists. The same reason the ordinary
    /// briefing says which of the objective's two sources it is showing.
    /// </para>
    /// <para>
    /// The findings it asks for are shaped like the original report's, deliberately: the reviewer
    /// walks a scoped lap's findings with the same skill and directs them with the same two
    /// commands, so a different shape would be a second thing to learn for no gain.
    /// </para>
    /// </summary>
    private static void AppendSinceMyReviewSection(
        StringBuilder prompt, ReviewLapBriefing briefing, ScopedReviewPacket scoped)
    {
        prompt.AppendLine("## What has changed since your review");
        prompt.AppendLine();
        prompt.AppendLine(
            $"This is a **scoped lap**. The reviewer ({OneLine(scoped.ReviewerLogin)}) has already reviewed "
            + "this pull request once, and this briefing is only what has arrived since: replies on the "
            + "threads they opened, and the commits pushed after their review. The objective, the blast "
            + "radius, the CI results and the platform's own earlier findings report are deliberately NOT "
            + "here — they were read in the first lap and re-reading them is what this flag exists to avoid. "
            + "Do not reason as though the packet below were the whole pull request; when something in it "
            + "needs wider context, go and read that context in the checkout rather than assuming it away.");
        prompt.AppendLine();
        prompt.AppendLine(
            scoped.ReviewedHeadSha.IsNotBlank()
                ? $"Their review was posted against `{ShortSha(scoped.ReviewedHeadSha)}`; the head is now "
                  + $"`{ShortSha(scoped.CurrentHeadSha ?? string.Empty)}`."
                : "The platform has no record of which commit their review was posted against, so the code "
                  + "half of this packet is the commits it could observe rather than a range pinned to their "
                  + "review. Say so if it matters to a finding.");
        prompt.AppendLine();

        // Stated up here rather than left to be inferred from an empty packet, because it is the
        // one thing that can summon this lap with nothing at all in either half below: an author
        // who resolves the reviewer's threads themselves and re-requests the review, with no reply
        // and no push, is asking them to look again and the packet has nothing to show for it
        // (independent pre-PR review, cycle 1, adversarial lens).
        if (scoped.ReReviewRequested)
        {
            prompt.AppendLine(
                "**The author has re-requested this review**, which is an explicit ask to look again "
                + "whatever the packet below turns out to hold — a re-request with no reply and no push is "
                + "still an ask.");
            prompt.AppendLine();
        }

        prompt.AppendLine("### Thread replies");
        prompt.AppendLine();
        if (scoped.ThreadPageTruncated)
        {
            // Said before the counts rather than after them, because a count read first is a count
            // believed: this pull request carries more review threads than one provider page holds,
            // so every number below is a floor and threads of the reviewer's own may be missing
            // from this packet outright.
            prompt.AppendLine(
                "**This pull request carries more review threads than the provider's own page cap can "
                + "return (100), so the thread half of this packet is incomplete.** Every count below is a "
                + "floor, and threads the reviewer opened may be missing from it entirely — an absent thread "
                + "here does NOT mean it went quiet. Read them on GitHub before treating any silence below "
                + $"as an answer: `gh pr view {OneLine(briefing.PullRequest.Repository)}#"
                + $"{briefing.PullRequest.Number.ToString(CultureInfo.InvariantCulture)} --comments`.");
            prompt.AppendLine();
        }

        if (scoped.Threads.Count == 0)
        {
            prompt.AppendLine(
                "None of the reviewer's own threads have moved since their review"
                + (scoped.UnchangedThreadCount, scoped.ThreadPageTruncated) switch
                {
                    ( > 0, true) => $" — all {Count(scoped.UnchangedThreadCount)} of theirs that could be read are unchanged.",
                    ( > 0, false) => $" (all {Count(scoped.UnchangedThreadCount)} of them are unchanged).",
                    (_, true) => " — none of theirs were inside the page that could be read.",
                    (_, false) => " — they opened none.",
                }
                // Never asserted as a fact when a re-request is what summoned the lap: the code
                // half can be empty too, and telling a session to go and find the cause there
                // would send it hunting something that does not exist.
                + (scoped.ReReviewRequested
                    ? " What prompted this lap may be nothing more than the re-request above; say so plainly"
                      + " if the code half below is empty as well."
                    : " Whatever prompted this lap is in the code half below."));
            prompt.AppendLine();
        }
        else
        {
            prompt.AppendLine(
                $"{Count(scoped.Threads.Count)} of the reviewer's threads moved"
                + (scoped.UnchangedThreadCount > 0
                    ? $"; {Count(scoped.UnchangedThreadCount)} more are unchanged and are not shown."
                    : ".")
                + " Every reply is verbatim.");
            prompt.AppendLine();
            foreach (ScopedReviewThreadDelta thread in scoped.Threads)
            {
                prompt.AppendLine(
                    $"#### {OneLine(thread.Location)} — {(thread.IsResolved ? "resolved" : "still unresolved")}");
                prompt.AppendLine();
                if (thread.NewComments.Count == 0 && thread.UnreadCommentCount == 0)
                {
                    prompt.AppendLine("(No new comment; the thread's own state is what changed.)");
                    prompt.AppendLine();
                    continue;
                }

                foreach (string comment in thread.NewComments)
                {
                    prompt.AppendLine(Block(comment));
                    prompt.AppendLine();
                }

                if (thread.UnreadCommentCount > 0)
                {
                    // The unread tail is the NEWEST comments — the page is read from the front so
                    // the thread's opener, which is what makes it the reviewer's, is never the one
                    // dropped. On a scoped lap that is the worst possible loss to leave silent: the
                    // latest word in the thread is the thing the reviewer came back for.
                    prompt.AppendLine(
                        $"(**{thread.UnreadCommentCount.ToString(CultureInfo.InvariantCulture)} further "
                        + $"comment(s) on this thread are past the provider's own page cap and are NOT shown "
                        + "here** — and they are the most recent ones, so the last word in this thread is not "
                        + "above. Read the thread on GitHub before drawing a conclusion from it.)");
                    prompt.AppendLine();
                }
            }
        }

        prompt.AppendLine("### Commits pushed since your review");
        prompt.AppendLine();
        if (scoped.NewCommits.Count == 0)
        {
            prompt.AppendLine("None were observed.");
        }
        else
        {
            foreach (string commit in scoped.NewCommits)
            {
                prompt.AppendLine($"- {OneLine(commit)}");
            }
        }

        prompt.AppendLine();
        if (scoped.DiffNote.IsNotBlank())
        {
            prompt.AppendLine(scoped.DiffNote);
            prompt.AppendLine();
        }

        if (scoped.Diff.IsNotBlank())
        {
            prompt.AppendLine(Block(scoped.Diff));
            prompt.AppendLine();
        }

        prompt.AppendLine("### What to produce");
        prompt.AppendLine();
        prompt.AppendLine(
            "Read the packet above and report findings in the same shape the first lap's report used: one "
            + "heading per finding, each naming the file and line it is about, what is wrong, and how "
            + "confident you are. A reply that answers the original finding correctly is itself a finding "
            + "worth stating — \"this one is addressed\" is what lets the reviewer resolve the thread. Nothing "
            + "you write is posted anywhere; the reviewer directs each finding themselves, exactly as they "
            + "did the first time.");
        prompt.AppendLine();
    }

    /// <summary>"1 thread" / "3 threads", spelled once so every sentence in the scoped section agrees.</summary>
    private static string Count(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "thread" : "threads")}";

    private static void AppendFindingsReportSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        prompt.AppendLine("## The platform's own findings report");
        prompt.AppendLine();
        if (briefing.FindingsReport.IsBlank())
        {
            prompt.AppendLine(
                "The automated review of this pull request has not produced a findings report yet, so there "
                + "is none to show. The reviewer got here before the machines did, which is a normal way to "
                + $"run a lap — `h9k task show {briefing.TaskId}` says where the automated run stands.");
            prompt.AppendLine();
            return;
        }

        prompt.AppendLine(
            "Two automated lenses have already read this pull request — one adversarial, hunting for defects "
            + "with no knowledge of the intent, one conformance, checking the diff against the stated "
            + "objective. Their merged report is below, verbatim. Nothing in it has been posted to the pull "
            + "request, and nothing in it is a verdict: it is what two machines found, for the reviewer to "
            + "weigh alongside their own reading.");
        prompt.AppendLine();
        prompt.AppendLine(Block(briefing.FindingsReport));
        prompt.AppendLine();
    }

    private static void AppendAuthorRunSection(StringBuilder prompt, ReviewLapAuthorRun? authorRun)
    {
        if (authorRun is null)
        {
            return;
        }

        prompt.AppendLine("## What the author's own run already settled");
        prompt.AppendLine();
        prompt.AppendLine(
            "This node can read the run that produced this pull request, so the platform's own account of it "
            + "is available. It is an account of what the pipeline did, not a vouching for the diff.");
        prompt.AppendLine();
        prompt.AppendLine(
            $"- Review loop ended: {OneLine(authorRun.Settlement)} "
            + $"({authorRun.ResidualsFixed} residual(s) fixed, {authorRun.ResidualsRouted} routed elsewhere)");
        if (authorRun.UnclaimedResiduals.Count > 0)
        {
            prompt.AppendLine(
                $"- Unclaimed residuals ({authorRun.UnclaimedResiduals.Count}) — findings the platform's own "
                + "review raised and then did not fix on this branch, either because it ran out of cycles or "
                + "because it graded them below the bar of the cycle that found them. These came from a hunt "
                + "with no knowledge of the author's intent and nobody has vouched for them since, which is "
                + "why they are named here rather than left in the run history:");
            foreach (string residual in authorRun.UnclaimedResiduals)
            {
                prompt.AppendLine($"  - {OneLine(residual)}");
            }
        }
        else
        {
            prompt.AppendLine("- Unclaimed residuals: none recorded");
        }

        if (authorRun.Rulings.Count > 0)
        {
            prompt.AppendLine(
                $"- Disputes and rulings ({authorRun.Rulings.Count}) — where a human already settled a "
                + "question on this diff. A ruling is a record of a decision, not a reason a reviewer cannot "
                + "reach a different one:");
            foreach (string ruling in authorRun.Rulings)
            {
                prompt.AppendLine($"  - {OneLine(ruling)}");
            }
        }
        else
        {
            prompt.AppendLine("- Disputes and rulings: none recorded");
        }

        prompt.AppendLine();
    }

    private static void AppendWorkingArrangementSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        prompt.AppendLine("## Where you are");
        prompt.AppendLine();
        if (briefing.WorktreePath.IsNotBlank())
        {
            prompt.AppendLine(
                $"A read-only checkout of this pull request's head is at `{briefing.WorktreePath}` — a detached "
                + "worktree of project "
                + $"{OneLine(briefing.ProjectName)}'s clone at `{briefing.RepositoryPath}`, with no local branch, "
                + "so there is nothing here that could be pushed by accident. The base branch is available as "
                + $"`origin/{briefing.PullRequest.BaseRefName}`, so "
                + $"`git diff origin/{briefing.PullRequest.BaseRefName}...HEAD` is the pull request's own range.");
        }
        else
        {
            prompt.AppendLine(
                "No checkout was made for this lap (`--no-worktree`): the reviewer is testing against a "
                + "deployed environment rather than reading the code locally. If they later want the code in "
                + "front of them, say so — the lap can be re-run without that flag.");
        }

        prompt.AppendLine();
    }

    private static void AppendRulesSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        prompt.AppendLine("## Working rules");
        prompt.AppendLine();
        prompt.AppendLine(
            "- **The briefing above is the whole of what you volunteer.** Do not open with test scenarios, "
            + "areas of concern, a suggested review order, or a summary of what you think of the diff. The "
            + "reviewer forms their own view; a briefing that hands them one replaces their judgment with the "
            + "platform's while looking like help. Once they ask — for scenarios, for a second read of a "
            + "function, for an opinion — give it fully and directly.");
        prompt.AppendLine(
            "- **Help with whatever they ask for.** Getting the project running locally, running its suites, "
            + "reading a subsystem out loud, writing an end-to-end test that exercises the change — all of it "
            + "is yours to do on request.");
        prompt.AppendLine(
            "- **Never commit to or push this pull request's branch.** It is somebody else's work. The "
            + "checkout you are in is detached with no local branch precisely so there is nothing to push, and "
            + "`git push` is denied outright for this session. If you believe something must change on that "
            + "branch, that belongs in the review, not in a commit.");
        prompt.AppendLine(
            "- **Tests the reviewer writes go on a branch of their own.** Never onto this pull request's "
            + "branch. Offer to stack that branch on this pull request — `h9k task add --stacked-on` against "
            + "the authoring task, so its own pull request targets this one and gets retargeted "
            + "automatically when this one merges — and let them decide. Do not create the task without "
            + "being told to.");
        prompt.AppendLine(
            "- **You never post to GitHub.** Not a comment, not a review, not a reaction. The reviewer's "
            + "verdict is the one thing that reaches the pull request, and it goes through the two commands "
            + "below under their own login — run by them, in their own terminal, never by you. Every `gh pr` "
            + "verb that writes (`create`, `review`, `comment`, `edit`, `merge`, `close`, `reopen`, `ready`, "
            + "`lock`, `unlock`, `revert`, `update-branch`), every `gh issue` verb that writes (`create`, "
            + "`comment`, `edit`, `close`, `reopen`, `lock`, `unlock`, `delete`, `transfer`, `pin`, `unpin`, "
            + "`develop` — issues and pull requests share one number space and one resource, so "
            + "`gh issue comment <this pull request's number>` would comment on *it*), `gh api`, and those "
            + "two commands themselves (`h9k pr approve`, `h9k pr request-changes`) are all denied for this "
            + "session, so there is nothing to try: if the reviewer asks you to post something, to bring the "
            + "branch current, or to wrap the lap up, the answer is the command they run themselves, not "
            + "another route to the same endpoint. The reads (`gh pr view`, `gh pr diff`, `gh pr checks`, "
            + "`gh issue view`) are all yours.");
        prompt.AppendLine();
        WorkPromptBuilder.AppendExternalInteractionLoggingRule(prompt, briefing.TaskId);
    }

    private static void AppendClosingSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        prompt.AppendLine("## How this lap ends");
        prompt.AppendLine();
        prompt.AppendLine(
            "It ends when the reviewer says so, and never on its own — there is no point at which you "
            + "declare the review finished. Two commands end it, both of them theirs to run:");
        prompt.AppendLine();
        prompt.AppendLine($"- `h9k pr approve {briefing.TaskId} --note \"<what they want the approval to say>\"`");
        prompt.AppendLine(
            $"- `h9k pr request-changes {briefing.TaskId} --note \"<the summary>\" "
            + "--finding \"path:line: <what is wrong>\"` (repeat `--finding` per line comment)");
        prompt.AppendLine();
        prompt.AppendLine(
            "Either one posts the GitHub review on the pull request's head under the reviewer's own login, "
            + "records the verdict on this task, releases the checkout, and closes the task out. Both are "
            + "denied for this session, deliberately: they are printed here so you can hand the reviewer the "
            + "exact line to run, not so you can run it. If they ask you to draft the note or the findings, "
            + "draft them and hand them over — running the command is theirs.");
        prompt.AppendLine();
    }

    /// <summary>
    /// The one-line "across N surfaces" phrase, kept beside the grouping it describes so the two
    /// cannot drift into disagreeing about how many there are.
    /// </summary>
    private static string DescribeSurfaces(IReadOnlyList<PullRequestFileChange> files)
    {
        int surfaces = GroupBySurface(files).Count();
        return surfaces == 1 ? "1 surface" : $"{surfaces.ToString(CultureInfo.InvariantCulture)} surfaces";
    }

    /// <summary>
    /// Which surface a file belongs to. Two segments deep rather than one, because one segment
    /// collapses every project in a <c>src/</c> tree into a single "src" bucket and a blast
    /// radius that cannot tell the CLI from the daemon is not a blast radius. A file at the root
    /// is its own surface, named by the file, since there is no directory to group it under.
    /// </summary>
    private static IEnumerable<IGrouping<string, PullRequestFileChange>> GroupBySurface(
        IReadOnlyList<PullRequestFileChange> files) =>
        files
            .GroupBy(file => SurfaceOf(file.Path))
            .OrderByDescending(surface => surface.Sum(file => file.Additions + file.Deletions))
            .ThenBy(surface => surface.Key, StringComparer.Ordinal);

    internal static string SurfaceOf(string path)
    {
        string[] segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length switch
        {
            0 => "(unnamed)",
            1 => segments[0],
            _ => $"{segments[0]}/{segments[1]}",
        };
    }

    private static string ShortSha(string sha) => sha.Length > 12 ? sha[..12] : sha.IsNotBlank() ? sha : "(unknown)";

    /// <summary>
    /// Relayed text on one line. Everything this briefing quotes — a pull request title, a check
    /// name, a criterion, a finding — was written by somebody other than this platform, so it is
    /// defused the same way <c>PullRequestBody</c> defuses what it writes: layout characters
    /// become spaces, and anything a sink would obey rather than display is dropped.
    /// </summary>
    private static string OneLine(string text) => RelayedText.OneLine(text).Trim();

    /// <summary>The same defusal for text that is prose and keeps its paragraphs.</summary>
    private static string Block(string text) => RelayedText.Printable(text);
}
