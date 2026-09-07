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
    ReviewLapAuthorRun? AuthorRun);

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

        AppendObjectiveSection(prompt, briefing);
        AppendBlastRadiusSection(prompt, pullRequest);
        AppendChecksSection(prompt, pullRequest);
        AppendFindingsReportSection(prompt, briefing);
        AppendAuthorRunSection(prompt, briefing.AuthorRun);
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
            + "`lock`, `unlock`, `revert`, `update-branch`), `gh api`, and those two commands themselves "
            + "(`h9k pr approve`, `h9k pr request-changes`) are all denied for this session, so there is "
            + "nothing to try: if the reviewer asks you to post something, to bring the branch current, or "
            + "to wrap the lap up, the answer is the command they run themselves, not another route to the "
            + "same endpoint. The reads (`gh pr view`, `gh pr diff`, `gh pr checks`) are all yours.");
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
