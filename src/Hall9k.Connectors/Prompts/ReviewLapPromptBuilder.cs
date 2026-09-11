using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Storage;

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
/// <param name="WritingConventions">
/// The reviewing project's own house style, governing the <c>--note</c> and the findings this lap
/// drafts for the reviewer to post under their own login (task 412afe6c). Null falls back to the
/// platform default, which is what a caller with no project to read one from passes.
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
    ScopedReviewPacket? SinceMyReview = null,
    WritingConventions? WritingConventions = null);

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
    /// <summary>The package name this builder's own prose ships under in <c>.claude/templates</c>
    /// (and the canonical/release-payload equivalents) — also what <c>InstallCommand.ValidateReleasePayload</c>
    /// checks a <c>--from-release</c> payload actually carries, since a <c>templates/</c> directory
    /// present but empty or unrelated would otherwise validate while leaving this builder with
    /// nothing to load at runtime.</summary>
    public const string TemplateDirectory = "review-lap-prompt-builder";

    public static string Build(ReviewLapBriefing briefing)
    {
        StringBuilder prompt = new();
        PullRequestSurface pullRequest = briefing.PullRequest;
        const string file = $"{TemplateDirectory}/build.md";

        prompt.AppendLine(PromptTemplates.Load(file, "title"));
        prompt.AppendLine();
        string repoAndNumber = $"{pullRequest.Repository}#{pullRequest.Number}";
        prompt.AppendLine(pullRequest.AuthorLogin.IsNotBlank()
            ? Fragment(file, "intro-with-author",
                ("RepoAndNumber", repoAndNumber),
                ("AuthorLogin", OneLine(pullRequest.AuthorLogin)),
                ("Title", OneLine(pullRequest.Title)))
            : Fragment(file, "intro-without-author",
                ("RepoAndNumber", repoAndNumber),
                ("Title", OneLine(pullRequest.Title))));
        if (pullRequest.Url is not null)
        {
            prompt.AppendLine();
            prompt.AppendLine(pullRequest.Url.ToString());
        }

        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "range",
            ("BaseRef", pullRequest.BaseRefName),
            ("HeadRef", pullRequest.HeadRefName),
            ("HeadSha", ShortSha(pullRequest.HeadSha))));
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
        const string file = $"{TemplateDirectory}/objective.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        if (briefing.StatedObjective.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "with-task-intro",
                ("IdParenthetical", briefing.AuthorTaskShortId.IsNotBlank() ? $" ({briefing.AuthorTaskShortId})" : string.Empty)));
            prompt.AppendLine();
            prompt.AppendLine(Block(briefing.StatedObjective));
            if (briefing.AcceptanceCriteria.Count > 0)
            {
                prompt.AppendLine();
                prompt.AppendLine(PromptTemplates.Load(file, "acceptance-criteria-heading"));
                prompt.AppendLine();
                foreach (string criterion in briefing.AcceptanceCriteria)
                {
                    prompt.AppendLine($"- {OneLine(criterion)}");
                }
            }
            else
            {
                prompt.AppendLine();
                prompt.AppendLine(PromptTemplates.Load(file, "no-acceptance-criteria"));
            }
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "without-task"));
            prompt.AppendLine();
            prompt.AppendLine(briefing.PullRequest.Body.IsNotBlank()
                ? Block(briefing.PullRequest.Body)
                : PromptTemplates.Load(file, "no-description"));
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
        const string templateFile = $"{TemplateDirectory}/blast-radius.md";
        prompt.AppendLine(PromptTemplates.Load(templateFile, "heading"));
        prompt.AppendLine();
        if (pullRequest.Files.Count == 0)
        {
            // The count is stated only when there IS one. ChangedFiles falls back to the list's
            // own length, which is zero here, and "0 file(s)" in the branch whose whole point is
            // admitting a gap reads as "this pull request changes nothing" — the opposite of what
            // is known (self-review, round two).
            prompt.AppendLine(pullRequest.ChangedFiles > 0
                ? Fragment(templateFile, "no-file-list-with-count",
                    ("ChangedFiles", pullRequest.ChangedFiles.ToString(CultureInfo.InvariantCulture)),
                    ("Additions", pullRequest.Additions.ToString(CultureInfo.InvariantCulture)),
                    ("Deletions", pullRequest.Deletions.ToString(CultureInfo.InvariantCulture)))
                : Fragment(templateFile, "no-file-list-no-count",
                    ("Additions", pullRequest.Additions.ToString(CultureInfo.InvariantCulture)),
                    ("Deletions", pullRequest.Deletions.ToString(CultureInfo.InvariantCulture))));
            prompt.AppendLine();
            return;
        }

        string fileWord = pullRequest.ChangedFiles == 1 ? "file" : "files";
        prompt.AppendLine(Fragment(templateFile, "summary",
            ("FileCount", pullRequest.ChangedFiles.ToString(CultureInfo.InvariantCulture)),
            ("FileWord", fileWord),
            ("Additions", pullRequest.Additions.ToString(CultureInfo.InvariantCulture)),
            ("Deletions", pullRequest.Deletions.ToString(CultureInfo.InvariantCulture)),
            ("Surfaces", DescribeSurfaces(pullRequest.Files))));
        // GitHub paginates the files list, so a very large pull request comes back with an honest
        // count and a short list. Said out loud rather than left to be inferred from a grouping
        // that silently covers part of the change (AGENTS.md, never guess at unobserved facts):
        // a reviewer who reads the groups below as the whole blast radius would be wrong, and
        // nothing else on this screen would tell them (self-review, round one).
        if (pullRequest.ChangedFiles > pullRequest.Files.Count)
        {
            prompt.AppendLine();
            prompt.AppendLine(Fragment(templateFile, "truncated",
                ("ServedCount", pullRequest.Files.Count.ToString(CultureInfo.InvariantCulture)),
                ("TotalCount", pullRequest.ChangedFiles.ToString(CultureInfo.InvariantCulture))));
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
        const string file = $"{TemplateDirectory}/checks.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        if (!pullRequest.ChecksObserved)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "not-observed"));
            prompt.AppendLine();
            return;
        }

        if (pullRequest.Checks.Count == 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "empty"));
            prompt.AppendLine();
            return;
        }

        foreach (PullRequestCheck check in pullRequest.Checks)
        {
            string name = check.Workflow.IsNotBlank() ? $"{OneLine(check.Workflow)} / {OneLine(check.Name)}" : OneLine(check.Name);
            string outcome = check.Conclusion.IsNotBlank()
                ? OneLine(check.Conclusion)
                : $"{OneLine(check.Status)} — {PromptTemplates.Load(file, "no-conclusion-yet")}";
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
        const string file = $"{TemplateDirectory}/since-my-review.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "scope-intro", ("ReviewerLogin", OneLine(scoped.ReviewerLogin))));
        prompt.AppendLine();
        prompt.AppendLine(
            scoped.ReviewedHeadSha.IsNotBlank()
                ? Fragment(file, "head-known",
                    ("ReviewedShort", ShortSha(scoped.ReviewedHeadSha)),
                    ("CurrentShort", ShortSha(scoped.CurrentHeadSha ?? string.Empty)))
                : PromptTemplates.Load(file, "head-unknown"));
        prompt.AppendLine();

        // Stated up here rather than left to be inferred from an empty packet, because it is the
        // one thing that can summon this lap with nothing at all in either half below: an author
        // who resolves the reviewer's threads themselves and re-requests the review, with no reply
        // and no push, is asking them to look again and the packet has nothing to show for it
        // (independent pre-PR review, cycle 1, adversarial lens).
        if (scoped.ReReviewRequested)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "re-request-notice"));
            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "thread-replies-heading"));
        prompt.AppendLine();
        if (scoped.ThreadPageTruncated)
        {
            // Said before the counts rather than after them, because a count read first is a count
            // believed: this pull request carries more review threads than one provider page holds,
            // so every number below is a floor and threads of the reviewer's own may be missing
            // from this packet outright.
            prompt.AppendLine(Fragment(file, "page-truncated",
                ("RepoAndNumber",
                    $"{OneLine(briefing.PullRequest.Repository)}#{briefing.PullRequest.Number.ToString(CultureInfo.InvariantCulture)}")));
            prompt.AppendLine();
        }

        if (scoped.Threads.Count == 0)
        {
            prompt.AppendLine(
                PromptTemplates.Load(file, "none-moved-opening")
                + (scoped.UnchangedThreadCount, scoped.ThreadPageTruncated) switch
                {
                    ( > 0, true) => Fragment(file, "none-moved-unchanged-truncated", ("Count", Count(scoped.UnchangedThreadCount))),
                    ( > 0, false) => Fragment(file, "none-moved-unchanged-not-truncated", ("Count", Count(scoped.UnchangedThreadCount))),
                    (_, true) => PromptTemplates.Load(file, "none-moved-none-truncated"),
                    (_, false) => PromptTemplates.Load(file, "none-moved-none-not-truncated"),
                }
                // Never asserted as a fact when a re-request is what summoned the lap: the code
                // half can be empty too, and telling a session to go and find the cause there
                // would send it hunting something that does not exist.
                + PromptTemplates.Load(file, scoped.ReReviewRequested
                    ? "none-moved-rerequest-tail"
                    : "none-moved-no-rerequest-tail"));
            prompt.AppendLine();
        }
        else
        {
            prompt.AppendLine(
                Fragment(file, "moved-summary-opening", ("MovedCount", Count(scoped.Threads.Count)))
                + (scoped.UnchangedThreadCount > 0
                    ? Fragment(file, "moved-summary-with-unchanged", ("UnchangedCount", Count(scoped.UnchangedThreadCount)))
                    : PromptTemplates.Load(file, "moved-summary-no-unchanged"))
                + PromptTemplates.Load(file, "moved-summary-tail"));
            prompt.AppendLine();
            foreach (ScopedReviewThreadDelta thread in scoped.Threads)
            {
                prompt.AppendLine(
                    $"#### {OneLine(thread.Location)} — "
                    + PromptTemplates.Load(file, thread.IsResolved ? "thread-resolved" : "thread-unresolved"));
                prompt.AppendLine();
                if (thread.NewComments.Count == 0 && thread.UnreadCommentCount == 0)
                {
                    prompt.AppendLine(PromptTemplates.Load(file, "no-new-comment"));
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
                    prompt.AppendLine(Fragment(file, "unread-comment-notice",
                        ("UnreadCount", thread.UnreadCommentCount.ToString(CultureInfo.InvariantCulture))));
                    prompt.AppendLine();
                }
            }
        }

        prompt.AppendLine(PromptTemplates.Load(file, "commits-heading"));
        prompt.AppendLine();
        if (scoped.NewCommits.Count == 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "commits-none"));
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

        prompt.AppendLine(PromptTemplates.Load(file, "what-to-produce-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "what-to-produce-body"));
        prompt.AppendLine();
    }

    /// <summary>"1 thread" / "3 threads", spelled once so every sentence in the scoped section agrees.</summary>
    private static string Count(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "thread" : "threads")}";

    private static void AppendFindingsReportSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        const string file = $"{TemplateDirectory}/findings-report.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        if (briefing.FindingsReport.IsBlank())
        {
            prompt.AppendLine(Fragment(file, "not-yet", ("TaskId", briefing.TaskId.ToString())));
            prompt.AppendLine();
            return;
        }

        prompt.AppendLine(PromptTemplates.Load(file, "intro"));
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

        const string file = $"{TemplateDirectory}/author-run.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "intro"));
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "settlement-line",
            ("Settlement", OneLine(authorRun.Settlement)),
            ("Fixed", authorRun.ResidualsFixed.ToString(CultureInfo.InvariantCulture)),
            ("Routed", authorRun.ResidualsRouted.ToString(CultureInfo.InvariantCulture))));
        if (authorRun.UnclaimedResiduals.Count > 0)
        {
            prompt.AppendLine(Fragment(file, "unclaimed-intro",
                ("Count", authorRun.UnclaimedResiduals.Count.ToString(CultureInfo.InvariantCulture))));
            foreach (string residual in authorRun.UnclaimedResiduals)
            {
                prompt.AppendLine($"  - {OneLine(residual)}");
            }
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "unclaimed-none"));
        }

        if (authorRun.Rulings.Count > 0)
        {
            prompt.AppendLine(Fragment(file, "rulings-intro",
                ("Count", authorRun.Rulings.Count.ToString(CultureInfo.InvariantCulture))));
            foreach (string ruling in authorRun.Rulings)
            {
                prompt.AppendLine($"  - {OneLine(ruling)}");
            }
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "rulings-none"));
        }

        prompt.AppendLine();
    }

    private static void AppendWorkingArrangementSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        const string file = $"{TemplateDirectory}/working-arrangement.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(briefing.WorktreePath.IsNotBlank()
            ? Fragment(file, "with-worktree",
                ("WorktreePath", briefing.WorktreePath),
                ("ProjectName", OneLine(briefing.ProjectName)),
                ("RepositoryPath", briefing.RepositoryPath),
                ("BaseRef", briefing.PullRequest.BaseRefName))
            : PromptTemplates.Load(file, "without-worktree"));

        prompt.AppendLine();
    }

    private static void AppendRulesSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        const string file = $"{TemplateDirectory}/rules.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "volunteer"));
        prompt.AppendLine(PromptTemplates.Load(file, "help"));
        prompt.AppendLine(PromptTemplates.Load(file, "never-push"));
        prompt.AppendLine(PromptTemplates.Load(file, "own-branch"));
        prompt.AppendLine(PromptTemplates.Load(file, "never-post-github"));
        prompt.AppendLine();
        WorkPromptBuilder.AppendExternalInteractionLoggingRule(prompt, briefing.TaskId);
    }

    private static void AppendClosingSection(StringBuilder prompt, ReviewLapBriefing briefing)
    {
        const string file = $"{TemplateDirectory}/closing.md";
        string taskId = briefing.TaskId.ToString();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "intro"));
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "approve-command", ("TaskId", taskId)));
        prompt.AppendLine(Fragment(file, "request-changes-command", ("TaskId", taskId)));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "after-commands"));
        prompt.AppendLine();
        // The note and each finding are posted verbatim under the reviewer's own login, so a draft
        // that ignores the house style either goes out in the reviewer's name reading nothing like
        // them or costs them an edit (task 412afe6c). The platform re-checks the two mechanical
        // rules immediately before posting; this is what keeps a draft from needing that rescue.
        WorkPromptBuilder.AppendWritingConventions(
            prompt, string.Empty, briefing.WritingConventions ?? WritingConventions.Default,
            PromptTemplates.Load(file, "writing-conventions-lead-in"));
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

    /// <summary>A named fragment out of a template file, substituted. The <c>params</c> tuple
    /// array is this call site's whole parameter dictionary, spelled without one to build.</summary>
    private static string Fragment(string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.Load(file, name, values.ToDictionary(value => value.Key, value => value.Value));

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
