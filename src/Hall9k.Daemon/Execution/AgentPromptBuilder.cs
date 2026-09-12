using System.Globalization;
using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using static Hall9k.Connectors.Prompts.WorkPromptBuilder;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Assembles the agent's follow-up and pre-PR review prompts. The primary build/work prompt
/// (<see cref="Build"/>, a thin forward to <c>Hall9k.Connectors.Prompts.WorkPromptBuilder</c>)
/// and its exclusive helpers live there instead of here so <c>h9k task work</c> — which runs in
/// the CLI process and cannot reference <c>Hall9k.Daemon</c> — assembles the identical prompt a
/// headless dispatch would, through the same code rather than a parallel copy. Everything below
/// that stayed here (follow-up, fix-checks, rebase, and the pre-PR review prompts) still calls the
/// moved helpers unqualified via the <c>using static</c> above.
/// <para>
/// When the task's project has a home (backlog 47), the home is composed in too: the generated
/// AGENTS.md and the home's own <c>skills/</c> are named alongside the repo skills. The ruling
/// behind that is explicit — a dispatched agent never hunts for context, the dispatcher composes
/// everything into the briefing — and it is what makes the home's skills genuinely
/// model-agnostic: "read this file first" is the one instruction every runtime understands, so
/// nothing here depends on any vendor's directory-walking behaviour.
/// </para>
/// </summary>
public static class AgentPromptBuilder
{
    /// <summary>The package name this builder's own prose ships under in <c>.claude/templates</c>
    /// (and the canonical/release-payload equivalents), copying <c>ReviewLapPromptBuilder</c>'s own
    /// shape exactly (Decisions Log #PLACEHOLDER-6bb76ddf): one package per builder, published
    /// beside the canonical skills, never inside them.</summary>
    public const string TemplateDirectory = "agent-prompt-builder";

    /// <summary>A named fragment out of a template file, substituted. The <c>params</c> tuple
    /// array is this call site's whole parameter dictionary, spelled without one to build.</summary>
    private static string Fragment(string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.Load(file, name, values.ToDictionary(value => value.Key, value => value.Value));

    /// <summary>
    /// A named multi-line fragment, appended line by line via <see cref="PromptTemplates.AppendTemplate"/>
    /// so a template's own line endings never leak into the assembled prompt in place of
    /// <see cref="Environment.NewLine"/> — the same guarantee <see cref="Fragment"/> gets from a
    /// single <c>AppendLine</c> call, extended to a fragment spanning several source lines. The
    /// <c>params</c> tuple array is this call site's whole parameter dictionary.
    /// </summary>
    private static void AppendFragment(
        StringBuilder prompt, string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.AppendTemplate(prompt, file, name, values.ToDictionary(value => value.Key, value => value.Value));

    /// <summary>
    /// Forwards to the shared implementation — see the type doc above. <paramref name="baseBranch"/>
    /// is the branch this run's work sits on top of, which the caller resolved once at dispatch
    /// (<c>RunDispatched.BaseBranch</c>): the project's own for every ordinary run, a stacked
    /// child's parent branch instead. Null defers to the project's, which is what every caller
    /// that has no run to read one from passes. <paramref name="baseCommit"/> is that branch
    /// resolved to a commit at the cut (<c>RunDispatched.BaseCommit</c>), which is the fork point a
    /// stacked session's own recompose and self-review range are taken from — a ref cannot answer
    /// that once the parent is force-pushed (<c>WorkPromptBuilder.StackedForkPoint</c>).
    /// </summary>
    public static string Build(
        TaskDetails task,
        ProjectDetails project,
        string branch,
        string worktreePath,
        bool resumesPreviousWork = false,
        string? blockerContext = null,
        string? interactiveMilestoneAddress = null,
        string? baseBranch = null,
        string? baseCommit = null,
        TimeSpan? commandTimeout = null) =>
        WorkPromptBuilder.Build(
            task, project, branch, worktreePath, resumesPreviousWork, blockerContext, task.RetryReason,
            isHandback: task.ResumesFromHandback, interactiveMilestoneAddress: interactiveMilestoneAddress,
            baseBranch: baseBranch, baseCommit: baseCommit, commandTimeout: commandTimeout);

    /// <summary>
    /// The line a follow-up ends with when a review thread is a disagreement it cannot
    /// honestly judge (Decisions Log #62). The same RESOLUTION vocabulary the pre-PR fix
    /// session already answers in (log #23), because it is the same question — "is this
    /// mine to settle?" — asked about a thread instead of a finding.
    /// </summary>
    public const string DisputeMarker = "RESOLUTION: disputed";

    /// <summary>The other answer: every thread was handled, so the run proceeds to the gates.</summary>
    public const string ResolvedMarker = "RESOLUTION: fixed";

    /// <summary>
    /// The follow-up variant (PR closeout, Decisions Log #20): the agent resumes the task's
    /// existing PR branch to resolve review feedback via the repo-resident
    /// resolve-review-threads skill. How the fixes land is the commit style's call
    /// (Decisions Log #26): narrative folds them into the owning commits, append stacks
    /// them on top. The platform re-verifies and pushes; the PR updates in place.
    /// <para>
    /// Every unresolved thread is in scope, whoever opened it (Decisions Log #62), so the
    /// prompt has to teach the part that is not obvious from the threads themselves: which
    /// comments are a reviewer's and which are an earlier agent's, that a human's thread is
    /// handled with more care than a bot's, that an unjudgeable disagreement is parked
    /// rather than settled, and that a review body is answered where GitHub allows an answer
    /// at all.
    /// </para>
    /// </summary>
    /// <param name="baseCommit">
    /// This run's own recorded fork point, read for the narrative style's fixup-fold exactly as
    /// <see cref="BuildRebase"/> reads it — see <see cref="AppendCommitStyleRules"/>'s own
    /// parameter for why <c>origin/&lt;parent&gt;</c> cannot be named there on a stacked child.
    /// </param>
    public static string BuildFollowUp(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl, CommitStyle commitStyle,
        string? interactiveMilestoneAddress = null, string? baseBranch = null, string? baseCommit = null,
        TimeSpan? commandTimeout = null)
    {
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        const string file = $"{TemplateDirectory}/follow-up.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "follow-up-reason", ("Reason", task.FollowUpReason)));
            prompt.AppendLine();
        }

        AppendOperatorGuidanceSection(prompt, task);

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        AppendReviewerAttributionRules(prompt);
        AppendThreadTriageRules(prompt, project.Name);
        AppendThreadHandlingRules(prompt, project);
        AppendThreadDisputeRules(prompt);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("Branch", branch));
        AppendRetainedWorktreeNote(prompt);
        AppendFragment(prompt, file, "resolve-skill", ("PullRequestUrl", pullRequestUrl));
        AppendThreadTextBoundaryRule(prompt);
        AppendCommitStyleRules(
            prompt, commitStyle, effectiveBaseBranch,
            ResumedStackedFold(project, effectiveBaseBranch, baseCommit));
        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary", ("ThreadDispositionSummaryMarker", ThreadDispositionSummaryMarker));
        // R8's outbound milestones (task: agents on an interactive-mode task report outbound):
        // this follow-up dispatches under SessionRoleName.Build (RunLauncher's own sessionRole
        // split), so it is a dispatched build session by the same discriminator the rest of this
        // feature keys on, exactly like the fresh-dispatch build prompt in WorkPromptBuilder.Build.
        // Every follow-up starts a brand-new RunAggregate stream, so interactiveMilestoneAddress is
        // null on every production path today — the same honest "nobody has registered yet" case
        // WorkPromptBuilder.Build's own comment documents for a fresh headless build (independent
        // pre-PR review, cycle 1, conformance lens).
        if (task.InteractiveModeEnabled)
        {
            AppendOutboundMilestoneRules(prompt, "build", OutboundMilestone.Build, interactiveMilestoneAddress);
        }

        // A reopened task's follow-up run is the run that reaches true closeout, so it is the
        // run whose handoff travels (Decisions Log #36) — it covers the whole task, not only
        // this leg's fixes.
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// The changes-requested variant (task: a changes-requested pull-request review from a human
    /// becomes a fix lap): a PERSON formally requested changes on the pull request's current head,
    /// and this session answers that review. Modeled on <see cref="BuildFollowUp"/> — same branch,
    /// same commit style, same platform push — with one deliberate difference that is the whole
    /// reason it exists.
    /// <para>
    /// <see cref="BuildFollowUp"/> tells a session to argue its own case in the thread and resolve
    /// it. That is right for Copilot and for the automated dispute path a bot's findings have
    /// always taken. It is wrong for a person: telling a colleague they are mistaken is a social
    /// act the implementer owns, so here a disagreement posts NOTHING, resolves nothing, and parks
    /// with a drafted reply the human sends, edits, or drops (Brian's ruling, 2026-09-06 12:15).
    /// </para>
    /// <para>
    /// The findings are handed over rather than hunted for, in the shape every platform review
    /// finding takes (<c>ReviewResultParser.FindingMarker</c>): closeout already read the review's
    /// body and every inline comment, so a session that went looking for them itself could only
    /// read a staler copy of the same thing.
    /// </para>
    /// </summary>
    /// <param name="baseCommit">
    /// This run's own recorded fork point, read for the narrative style's fixup-fold on the same
    /// terms <see cref="BuildFollowUp"/>'s own parameter states.
    /// </param>
    public static string BuildReviewRequestedChanges(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl, CommitStyle commitStyle,
        string? interactiveMilestoneAddress = null, string? baseBranch = null, string? baseCommit = null,
        TimeSpan? commandTimeout = null)
    {
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        const string file = $"{TemplateDirectory}/review-requested-changes.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "follow-up-reason", ("Reason", task.FollowUpReason)));
            prompt.AppendLine();
        }

        AppendOperatorGuidanceSection(prompt, task);

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        AppendProjectHome(prompt, project);
        AppendChangesRequestedFindings(prompt, task);
        AppendChangesRequestedHandlingRules(prompt, project);
        AppendChangesRequestedDisagreementRules(prompt);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("Branch", branch));
        AppendRetainedWorktreeNote(prompt);
        AppendFragment(prompt, file, "work-from-findings");
        AppendFragment(prompt, file, "resolve-skill");
        AppendThreadTextBoundaryRule(prompt);
        AppendCommitStyleRules(
            prompt, commitStyle, effectiveBaseBranch,
            ResumedStackedFold(project, effectiveBaseBranch, baseCommit));
        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary");
        // R8's outbound milestones, on the identical terms BuildFollowUp's own comment states:
        // this follow-up dispatches under SessionRoleName.Build, still a build-role session, and
        // starts a brand-new RunAggregate stream, so interactiveMilestoneAddress is null on every
        // production path today.
        if (task.InteractiveModeEnabled)
        {
            AppendOutboundMilestoneRules(prompt, "build", OutboundMilestone.Build, interactiveMilestoneAddress);
        }

        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// The review itself, handed over as findings in the shape every platform review finding takes
    /// (task: a changes-requested pull-request review from a human becomes a fix lap) — so a
    /// session that has read a pre-PR review's findings recognizes these on sight.
    /// <para>
    /// Two tags are deliberately absent from these headers, and their absence is the point. There
    /// is no <c>severity=</c>: the platform's severity anchors are what a review PASS grades
    /// against, and a person who requested changes graded nothing — inventing a grade for them
    /// would be a guess written into the one place the session decides how much a finding is worth
    /// (AGENTS.md's never-guess rule). There is no <c>scope=</c> either, for the same reason. What
    /// is stated instead is the standing that actually applies: a human requested changes, so
    /// every finding here is one the reviewer requires an answer to.
    /// </para>
    /// </summary>
    private static void AppendChangesRequestedFindings(StringBuilder prompt, TaskDetails task)
    {
        const string file = $"{TemplateDirectory}/review-requested-changes.md";
        prompt.AppendLine(PromptTemplates.Load(file, "findings-heading"));
        prompt.AppendLine();
        if (task.ChangesRequestedReviews.Count == 0)
        {
            // Never reached from a dispatch (TaskDecider.Reopen refuses a changes-requested lap
            // with no review), so this is the honest reading of a task whose reopen predates this
            // vocabulary or whose record was lost — never a fabricated finding.
            AppendFragment(prompt, file, "no-reviews");
            prompt.AppendLine();
            return;
        }

        AppendFragment(prompt, file, "findings-intro", ("FindingMarker", ReviewResultParser.FindingMarker));
        prompt.AppendLine();

        foreach (ChangesRequestedReview review in task.ChangesRequestedReviews)
        {
            string submitted = review.SubmittedAt is { } at
                ? at.ToString("u", CultureInfo.InvariantCulture)
                : PromptTemplates.Load(file, "time-not-reported");
            prompt.AppendLine(Fragment(file, "review-heading", ("Reviewer", review.Reviewer), ("Submitted", submitted)));
            prompt.AppendLine();
            prompt.AppendLine(Fragment(file, "review-url", ("ReviewUrl", review.ReviewUrl)));
            prompt.AppendLine();
            if (review.Findings.Count == 0)
            {
                // Deliberately NOT stated as "the reviewer said nothing": closeout's own thread
                // read is capped at the first 100 threads, so a review whose comments all sit past
                // that cap also arrives here with no findings, and a reviewer who did state what
                // they wanted would be reported as silent (independent pre-PR review, cycle 1,
                // adversarial lens). What was actually observed is that closeout read none — so
                // the session is pointed at the review itself before concluding either way.
                AppendFragment(prompt, file, "no-findings-in-review");
                prompt.AppendLine();
                continue;
            }

            foreach (ChangesRequestedFinding finding in review.Findings)
            {
                List<string> tags = [];
                if (finding.Location.IsNotBlank())
                {
                    tags.Add($"at={finding.Location}");
                }

                if (finding.ThreadId.IsNotBlank())
                {
                    tags.Add($"thread={finding.ThreadId}");
                }

                prompt.AppendLine(tags.Count > 0
                    ? $"{ReviewResultParser.FindingMarker} {string.Join("; ", tags)}"
                    : Fragment(file, "no-location-finding", ("FindingMarker", ReviewResultParser.FindingMarker)));
                prompt.AppendLine(finding.Body);
                prompt.AppendLine();
            }
        }
    }

    /// <summary>
    /// How a finding the session agrees with is answered. The same care asymmetry
    /// <see cref="AppendThreadHandlingRules"/> teaches, narrowed to the one case where the reviewer
    /// is known to be a person: there is no bot half to state, because a bot's review never reaches
    /// this prompt.
    /// </summary>
    private static void AppendChangesRequestedHandlingRules(StringBuilder prompt, ProjectDetails project)
    {
        const string file = $"{TemplateDirectory}/review-requested-changes.md";
        prompt.AppendLine(PromptTemplates.Load(file, "handling-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "handling-intro"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "handling-fix");
        AppendFragment(prompt, file, "handling-question");
        AppendFragment(prompt, file, "handling-body-comment");
        AppendFragment(prompt, file, "handling-never-open-thread");
        AppendWritingConventions(
            prompt, string.Empty, project.WritingConventions, PromptTemplates.Load(file, "writing-conventions-lead-in"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "hides-comments");
        prompt.AppendLine();
    }

    /// <summary>
    /// The one rule this prompt exists for (task: a changes-requested pull-request review from a
    /// human becomes a fix lap). A disagreement with a person is not posted by this session, by
    /// any other agent, or by an orchestrator — it is drafted here and sent, edited, or dropped by
    /// the implementer through <c>h9k review resolve</c> (Brian's ruling, 2026-09-06 12:15).
    /// <para>
    /// The marker is the same <c>RESOLUTION: disputed</c> vocabulary the thread-dispute and
    /// rebase-dispute paths already answer in (<c>ReviewResultParser.ParseFixOutcome</c> reads it
    /// generically, whatever obstruction the follow-up was dispatched for) — reused rather than
    /// reinvented, with the structured block above it being what is new.
    /// </para>
    /// </summary>
    private static void AppendChangesRequestedDisagreementRules(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/review-requested-changes.md";
        prompt.AppendLine(PromptTemplates.Load(file, "disagreement-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "disagreement-intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "fix-first");
        prompt.AppendLine();
        prompt.AppendLine(Fragment(
            file, "block-header",
            ("DisagreementMarker", ReviewResultParser.DisagreementMarker),
            ("ExampleLocationPlaceholder", ReviewResultParser.ExampleLocationPlaceholder)));
        prompt.AppendLine(Fragment(file, "reviewer-asked-line", ("ReviewerAskedMarker", ReviewResultParser.ReviewerAskedMarker)));
        AppendFragment(prompt, file, "reasoning-line", ("DisagreementReasoningMarker", ReviewResultParser.DisagreementReasoningMarker));
        prompt.AppendLine(Fragment(file, "proposed-reply-marker-line", ("ProposedReplyMarker", ReviewResultParser.ProposedReplyMarker)));
        prompt.AppendLine(PromptTemplates.Load(file, "proposed-reply-body-line"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "dispute-marker-line", ("DisputeMarker", DisputeMarker));
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "fill-in-instructions",
            ("ExampleLocationPlaceholder", ReviewResultParser.ExampleLocationPlaceholder));
        prompt.AppendLine();
        AppendFragment(prompt, file, "park-platform");
        prompt.AppendLine();
        AppendFragment(prompt, file, "park-once");
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "resolved-line", ("ResolvedMarker", ResolvedMarker)));
        prompt.AppendLine();
    }

    /// <summary>
    /// The fix-the-CI variant (closeout monitor, Decisions Log #22): the agent resumes
    /// the task's existing PR branch to make the pull request's failing checks pass.
    /// Fixes land per the commit style, like any follow-up (Decisions Log #26).
    /// The platform re-verifies and pushes; the PR updates in place.
    /// </summary>
    /// <param name="baseCommit">
    /// This run's own recorded fork point, read for the narrative style's fixup-fold on the same
    /// terms <see cref="BuildFollowUp"/>'s own parameter states.
    /// </param>
    public static string BuildFixChecks(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl, CommitStyle commitStyle,
        string? interactiveMilestoneAddress = null, string? baseBranch = null, string? baseCommit = null,
        TimeSpan? commandTimeout = null)
    {
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        const string file = $"{TemplateDirectory}/fix-checks.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "follow-up-reason", ("Reason", task.FollowUpReason)));
            prompt.AppendLine();
        }

        AppendOperatorGuidanceSection(prompt, task);

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        AppendProjectHome(prompt, project);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("Branch", branch));
        AppendRetainedWorktreeNote(prompt);
        AppendFragment(prompt, file, "inspect-failures", ("PullRequestUrl", pullRequestUrl));
        prompt.AppendLine(PromptTemplates.Load(file, "fix-and-rerun"));
        AppendCommitStyleRules(
            prompt, commitStyle, effectiveBaseBranch,
            ResumedStackedFold(project, effectiveBaseBranch, baseCommit));
        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary");
        // R8's outbound milestones, on the identical terms BuildFollowUp's own comment states:
        // this follow-up dispatches under SessionRoleName.Checks, still a build-role session, and
        // starts a brand-new RunAggregate stream, so interactiveMilestoneAddress is null on every
        // production path today (independent pre-PR review, cycle 1, conformance lens).
        if (task.InteractiveModeEnabled)
        {
            AppendOutboundMilestoneRules(prompt, "build", OutboundMilestone.Build, interactiveMilestoneAddress);
        }

        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// The rebase-onto-main variant (backlog 44): the agent resumes the task's existing PR
    /// branch, which GitHub now reports as CONFLICTING against its base, and brings it current.
    /// Modeled on <see cref="BuildFixChecks"/> — same shape, different obstruction — but the
    /// conflict-resolution work needs judgment a checks fix does not, so it gets its own dispute
    /// path (the same RESOLUTION vocabulary <see cref="AppendThreadDisputeRules"/> already
    /// teaches, reused rather than reinvented: <c>ReviewResultParser.ParseFixOutcome</c> reads
    /// the marker generically, whatever obstruction the follow-up was dispatched for).
    /// <para>
    /// The verification instruction is explicit and not left to the platform's own re-verify
    /// (origin incident, 2026-08-22): this task's own first retry died on 7 test failures that
    /// were main-reconciliation fallout, not flakiness, because a rebase that looks clean can
    /// still break the build — the two branches' changes can each compile alone and conflict in
    /// behavior once combined.
    /// </para>
    /// </summary>
    /// <param name="humanResolution">
    /// Set when this session resumes a rebase whose previous attempt disputed a conflict and a
    /// human decided it (<c>h9k review resolve --needs-fixes</c>, <see cref="ReviewEngine"/>'s
    /// fix-session dispatch): their decision, inserted so the agent applies it rather than
    /// re-litigating the same conflict. That resumed-dispute dispatch is a Fix-role session
    /// (<c>SessionRoleName.Fix</c>), not the Build-role follow-up <see cref="RunLauncher"/>
    /// dispatches for an ordinary <c>FollowUpKind.Rebase</c> — the two callers are told apart
    /// below by this parameter's own presence, since a Build-role follow-up never carries one.
    /// </param>
    /// <param name="interactiveMilestoneAddress">
    /// R8's outbound-milestone address (task: agents on an interactive-mode task report outbound).
    /// <see cref="RunLauncher"/>'s own Build-role follow-up dispatch always passes null here (a
    /// brand-new <c>RunDispatched</c> stream, so nothing could have registered against it yet).
    /// <see cref="ReviewEngine"/>'s resumed-dispute dispatch is a Fix-role session instead — the two
    /// callers are told apart below by <paramref name="humanResolution"/>'s own presence — and passes
    /// its run's own <c>RegisteredInteractiveSessionName</c> here, the identical value
    /// <c>BuildReviewFix</c>'s own ordinary review-fix dispatch forwards.
    /// </param>
    /// <param name="baseCommit">
    /// This run's own recorded fork point (<c>RunDispatched.BaseCommit</c>), which is what the
    /// mechanics below name in place of <c>origin/&lt;base&gt;</c> when this branch is stacked on a
    /// parent branch rather than based on the project's own — see
    /// <see cref="AppendStackedRebaseRules"/> for why a plain merge-base rebase is the provably
    /// wrong operation there. Ignored for every ordinary run, whose prompt stays byte-identical.
    /// </param>
    public static string BuildRebase(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl, CommitStyle commitStyle,
        string? humanResolution = null, string? interactiveMilestoneAddress = null,
        bool? interactiveModeEnabledOverride = null, string? baseBranch = null, string? baseCommit = null,
        TimeSpan? commandTimeout = null)
    {
        bool interactiveModeEnabled = interactiveModeEnabledOverride ?? task.InteractiveModeEnabled;
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        bool isStacked = effectiveBaseBranch != project.BaseBranch;
        string? stackedForkPoint = WorkPromptBuilder.StackedForkPoint(project, effectiveBaseBranch, baseCommit);
        const string file = $"{TemplateDirectory}/rebase.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "intro-lead"));
        if (isStacked)
        {
            // The ordinary sentence below is untrue of a stacked child, and the difference is not
            // cosmetic: what moved is the branch this one is stacked ON, not the project's base, and
            // the mechanics further down turn on exactly that (independent pre-PR review, cycle 2,
            // adversarial lens).
            AppendFragment(prompt, file, "intro-stacked", ("BaseBranch", effectiveBaseBranch));
        }
        else
        {
            AppendFragment(prompt, file, "intro-unstacked", ("BaseBranch", effectiveBaseBranch));
        }

        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "follow-up-reason", ("Reason", task.FollowUpReason)));
            prompt.AppendLine();
        }

        AppendOperatorGuidanceSection(prompt, task);

        if (humanResolution.IsNotBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "human-decision-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "human-decision-intro");
            prompt.AppendLine();
            prompt.AppendLine(humanResolution);
            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        AppendProjectHome(prompt, project);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("Branch", branch));
        AppendRetainedWorktreeNote(prompt);
        AppendFragment(prompt, file, "rebase-skill-pointer");
        AppendFragment(prompt, file, "fetch-first", ("BaseBranch", effectiveBaseBranch));
        if (isStacked)
        {
            AppendStackedRebaseRules(prompt, branch, effectiveBaseBranch, stackedForkPoint);
        }
        else
        {
            AppendFragment(prompt, file, "plain-rebase", ("BaseBranch", effectiveBaseBranch));
        }

        AppendFragment(prompt, file, "replay-rules");
        AppendFragment(prompt, file, "no-markers");
        // A gate fix's fold cannot name `origin/<parent>` — and cannot name the recorded fork point
        // either, once the replay above has moved this branch off it: the boundary afterwards is the
        // commit the session replayed onto, which the replay's own first bullet had it record
        // (independent pre-PR review, cycle 2, adversarial lens).
        AppendRebaseVerificationRule(
            prompt, project, commitStyle, effectiveBaseBranch,
            fold: stackedForkPoint is null
                ? null
                : new FoldBoundary(
                    "<the commit you recorded before the replay>",
                    Fragment(file, "inline-fold-reason", ("EffectiveBaseBranch", effectiveBaseBranch)).Split('\n')));
        AppendFragment(prompt, file, "no-push");
        AppendRebaseDisputeRules(prompt);
        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary");
        // R8's outbound milestones. humanResolution's own presence is the signal this method's
        // doc already uses to tell the two callers apart: blank is RunLauncher's Build-role
        // follow-up dispatch (Build milestones, the same terms BuildFollowUp's own comment
        // states); non-blank is ReviewEngine's resumed-dispute Fix-role dispatch (Fix
        // milestones — this session is applying a human's decision on findings, the identical
        // shape of work BuildReviewFix's own ordinary dispatch reports under "fix").
        if (interactiveModeEnabled)
        {
            if (humanResolution.IsBlank())
            {
                AppendOutboundMilestoneRules(prompt, "build", OutboundMilestone.Build, interactiveMilestoneAddress);
            }
            else
            {
                AppendOutboundMilestoneRules(prompt, "fix", OutboundMilestone.Fix, interactiveMilestoneAddress);
            }
        }

        // A reopened task's follow-up run is the run that reaches true closeout, so it is the
        // run whose handoff travels (Decisions Log #36) — it covers the whole task, not only
        // this leg's rebase.
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// The one operation a stacked child's rebase follow-up may run, in place of the plain
    /// <c>git rebase origin/&lt;base&gt;</c> every ordinary run gets (independent pre-PR review,
    /// cycle 2, adversarial lens). A merge-base rebase against a parent branch is the provably
    /// wrong operation, for the reason <c>StackedParentWatch</c>'s own doc records and
    /// <c>ReviewEngine</c>'s pre-final-pass gate refuses outright over: the parent is routinely
    /// force-pushed while the child is in flight, which rewrites the history the child shares with
    /// it, so the merge base collapses below the child's own fork point and the rebase replays the
    /// child's copies of the parent's OLD commits against the parent's new ones. This prompt is
    /// reached when closeout could not observe the parent at all that sweep
    /// (<c>StackedParentVerdict.Unobservable</c>) and GitHub's own conflict read dispatched a
    /// judgment session instead of the mechanical replay — the parent may well have moved anyway,
    /// so the operation has to be the replay either way, keyed to the recorded fork point.
    /// <para>
    /// <paramref name="forkPointCommit"/> null is the honest dead end rather than a fallback: no
    /// commit names where the parent's work ends and this branch's begins, and
    /// <c>git merge-base</c> cannot recover it once the parent has been force-pushed, so there is
    /// nothing left to replay from that is not a guess (AGENTS.md's never-guess rule). That case
    /// goes to the dispute path, which is exactly where a human decides it.
    /// </para>
    /// </summary>
    private static void AppendStackedRebaseRules(
        StringBuilder prompt, string branch, string baseBranch, string? forkPointCommit)
    {
        const string file = $"{TemplateDirectory}/rebase.md";
        // The skill the bullet above points at walks a plain `git rebase origin/<base>`, which is
        // exactly the operation this branch may not run — so the pointer is qualified here rather
        // than left to contradict the mechanics that follow it.
        AppendFragment(prompt, file, "stacked-skill-caveat");
        if (forkPointCommit is null)
        {
            AppendFragment(prompt, file, "no-fork-point", ("BaseBranch", baseBranch));
            return;
        }

        AppendFragment(prompt, file, "record-commit", ("BaseBranch", baseBranch));
        AppendFragment(
            prompt, file, "replay-and-checks",
            ("BaseBranch", baseBranch), ("ForkPointCommit", forkPointCommit), ("Branch", branch));
    }

    /// <summary>
    /// Where the narrative commit style's fixup-fold rebases from, for a branch whose
    /// <c>origin/&lt;base&gt;</c> is not safe to name there: a stacked child's parent branch, which
    /// a force-push moves out from under the fold, collapsing its merge base below this branch's
    /// own commits so the fold rewrites the parent's already-reviewed work as this branch's
    /// authored history — and the tree-identity check cannot always catch that, since replaying
    /// the parent's own commits can leave the tree where it was (independent pre-PR review,
    /// cycle 2, adversarial lens).
    /// <para>
    /// <paramref name="Argument"/> is what follows <c>--autosquash</c>: a literal commit wherever
    /// the platform observed one, or a described placeholder where only the session can know it —
    /// a rebase that has just moved this branch onto a new base leaves the recorded fork point off
    /// this branch's line entirely, so the fold's boundary is the commit the session replayed onto,
    /// which it records for itself. <paramref name="Reason"/> is the caller's own explanation,
    /// appended verbatim (indentation included) under the command, because which boundary is right
    /// and why differs per prompt.
    /// </para>
    /// </summary>
    private sealed record FoldBoundary(string Argument, IReadOnlyList<string> Reason);

    /// <summary>
    /// The fold boundary for a follow-up that resumes a stacked child's branch without moving it —
    /// a review-feedback or failing-checks lap. The run's own recorded fork point is still this
    /// branch's boundary there, precisely because nothing in those prompts rebases the branch onto
    /// anything (a rebase prompt's own fold is a different commit; see <see cref="BuildRebase"/>).
    /// Null for every ordinary run and for a stacked run with no recorded fork point, both of which
    /// keep the <c>origin/&lt;base&gt;</c> wording exactly as it was.
    /// </summary>
    private static FoldBoundary? ResumedStackedFold(
        ProjectDetails project, string effectiveBaseBranch, string? baseCommit)
    {
        const string file = $"{TemplateDirectory}/rebase.md";
        return WorkPromptBuilder.StackedForkPoint(project, effectiveBaseBranch, baseCommit) is { } forkPoint
            ? new FoldBoundary(
                forkPoint,
                Fragment(file, "resumed-stacked-fold-reason", ("EffectiveBaseBranch", effectiveBaseBranch)).Split('\n'))
            : null;
    }

    /// <summary>
    /// The explicit re-verify instruction a rebase needs and a plain checks-fix does not
    /// (origin incident, 2026-08-22, cited on <see cref="BuildRebase"/>): a rebase that resolves
    /// every textual conflict can still combine two branches' changes into a behavior neither one
    /// had alone, so the platform's own re-verify after this session ends is not enough — the
    /// agent has to see the failure itself to fix its actual cause instead of a resubmitted
    /// flake theory.
    /// </summary>
    /// <param name="fold">
    /// Where a gate fix's fixup folds back to, when <c>origin/{baseBranch}</c> is not that place —
    /// see <see cref="FoldBoundary"/>. Null for every ordinary run, whose instruction stays
    /// byte-identical.
    /// </param>
    private static void AppendRebaseVerificationRule(
        StringBuilder prompt, ProjectDetails project, CommitStyle commitStyle, string baseBranch,
        FoldBoundary? fold = null)
    {
        const string file = $"{TemplateDirectory}/rebase.md";
        if (project.VerifyCommands.Count == 0)
        {
            AppendFragment(prompt, file, "no-verification-gates");
            return;
        }

        AppendFragment(prompt, file, "required-before-finish");
        foreach (VerifyCommand gate in project.VerifyCommands)
        {
            prompt.AppendLine($"    - `{gate.Command}`");
        }

        AppendFragment(prompt, file, "commit-fix-note");
        if (commitStyle == CommitStyle.Append)
        {
            AppendFragment(prompt, file, "append-style-fix");
        }
        else
        {
            AppendFragment(prompt, file, "narrative-style-fix-lead");
            prompt.AppendLine(Fragment(file, "fold-command", ("Argument", fold?.Argument ?? $"origin/{baseBranch}")));
            foreach (string line in fold?.Reason ?? [])
            {
                prompt.AppendLine(line);
            }

            AppendFragment(prompt, file, "trailing-editor-note");
        }
    }

    /// <summary>
    /// The park (backlog 44): the never-loop rule applies to a conflict exactly as it does to a
    /// review finding (<see cref="AppendThreadDisputeRules"/>) — a conflict where both sides
    /// changed the same behavior, not merely the same lines, is a human decision, and picking a
    /// side to make the rebase go through would silently drop one side's work. Reuses the same
    /// RESOLUTION marker vocabulary the review-feedback follow-up already teaches, so
    /// <c>RunSupervisor</c>'s existing dispute-park mechanism applies unchanged.
    /// </summary>
    private static void AppendRebaseDisputeRules(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/rebase.md";
        AppendFragment(prompt, file, "dispute-lead");
        prompt.AppendLine(Fragment(file, "dispute-marker-line", ("DisputeMarker", DisputeMarker)));
        AppendFragment(prompt, file, "dispute-mid", ("NeedsFixesFlag", "--needs-fixes"));
        prompt.AppendLine(Fragment(file, "resolved-line", ("ResolvedMarker", ResolvedMarker)));
        AppendFragment(prompt, file, "dispute-tail");
    }

    /// <summary>
    /// A narrow, mid-run recovery session (task: a run rebases its branch onto the current base
    /// branch): dispatched inside the build run's own lifecycle — no task reopen — after a plain
    /// <c>git rebase</c> onto the base branch conflicted immediately before the mandatory final
    /// full pass. Brian's 2026-09-04 ruling on scope: git conflicting is itself evidence that
    /// judgment is required, unlike the clean-apply case this session exists precisely because git
    /// could NOT resolve on its own. Deliberately not <see cref="BuildRebase"/> reused as-is: that
    /// prompt's own opening line ("the original task already shipped in the pull request above")
    /// assumes a follow-up run cut fresh for an existing PR, while this method is reached from the
    /// SAME run whether or not a pull request already exists for it — a fresh build's own first
    /// pre-final-pass rebase has none yet, but a follow-up run dispatched onto an already-open PR
    /// (<c>h9k pr resolve</c>) reaches this same mandatory-rebase step with one already live, so
    /// <paramref name="pullRequestUrl"/> is read from <c>TaskDetails.PullRequestUrl</c> rather than
    /// assumed either way (independent pre-PR review, cycle 1, adversarial lens).
    /// <para>
    /// The plain <c>git rebase origin/&lt;base&gt;</c> below is deliberately NOT given
    /// <see cref="BuildRebase"/>'s stacked replay variant, and that is a reachability argument
    /// rather than an omission (class sweep, independent pre-PR review, cycle 2): the only path
    /// here is <c>ReviewEngine.DispatchRebaseRecoverySessionAsync</c>, downstream of a
    /// pre-final-pass gate that refuses outright — before any git call, and before any phase that
    /// could re-dispatch this session — whenever the run's base is not the project's own. A
    /// stacked run therefore never reaches this prompt at all, and giving it a stacked branch here
    /// would describe an operation nothing dispatches.
    /// </para>
    /// </summary>
    public static string BuildPreFinalPassRebase(
        TaskDetails task, ProjectDetails project, string branch, CommitStyle commitStyle,
        string? pullRequestUrl, string? humanResolution = null, bool rebaseStillInProgress = false,
        string? baseBranch = null, TimeSpan? commandTimeout = null)
    {
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        const string file = $"{TemplateDirectory}/pre-final-pass-rebase.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        if (pullRequestUrl.IsNotBlank())
        {
            prompt.AppendLine($"Pull request: {pullRequestUrl}");
            prompt.AppendLine();
            AppendFragment(prompt, file, "with-pr-intro", ("BaseBranch", effectiveBaseBranch));
        }
        else
        {
            AppendFragment(prompt, file, "without-pr-intro", ("BaseBranch", effectiveBaseBranch));
        }

        prompt.AppendLine();

        if (humanResolution.IsNotBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "human-decision-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "human-decision-intro");
            prompt.AppendLine();
            prompt.AppendLine(humanResolution);
            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        AppendProjectHome(prompt, project);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "worktree-lead"));
        prompt.AppendLine(pullRequestUrl.IsNotBlank()
            ? Fragment(file, "worktree-with-pr", ("Branch", branch))
            : Fragment(file, "worktree-without-pr", ("Branch", branch)));
        if (rebaseStillInProgress)
        {
            AppendFragment(prompt, file, "rebase-in-progress");
        }
        else
        {
            AppendFragment(prompt, file, "rebase-not-in-progress");
        }
        AppendFragment(prompt, file, "fetch-first", ("BaseBranch", effectiveBaseBranch));
        AppendFragment(prompt, file, "plain-rebase", ("BaseBranch", effectiveBaseBranch));
        AppendFragment(prompt, file, "replay-rules");
        AppendFragment(prompt, file, "no-markers");
        AppendRebaseVerificationRule(prompt, project, commitStyle, effectiveBaseBranch);
        if (pullRequestUrl.IsNotBlank())
        {
            AppendFragment(prompt, file, "no-push-with-pr");
        }
        else
        {
            AppendFragment(prompt, file, "no-push-without-pr");
        }

        AppendRebaseDisputeRules(prompt);
        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary");
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// A narrow repair session dispatched when the Settling phase's own mandatory gate fails on a
    /// tree whose most recent recorded rebase was real (task: a pre-final-pass rebase that applies
    /// cleanly but breaks the mandatory gate gets a repair lap inside the same run instead of
    /// failing it) — the same dispatch shape <see cref="BuildPreFinalPassRebase"/> already uses
    /// (its own separate <see cref="RunSessionLeg.SettlingGateRepair"/> leg is
    /// <c>ReviewEngine</c>'s concern, not this builder's), over the gate's own output instead of a
    /// git conflict. No attempt is made here to claim
    /// the rebase caused the failure rather than a coincident commit or a verify-command change
    /// (the task's own criteria): the prompt names what actually happened — a rebase landed, then
    /// the mandatory gate failed — and lets the session's own investigation find the real cause.
    /// </summary>
    /// <param name="pullRequestUrl">
    /// Read from <c>TaskDetails.PullRequestUrl</c> rather than assumed, mirroring
    /// <see cref="BuildPreFinalPassRebase"/>'s own parameter: this session can be reached before a
    /// pull request ever opens (a fresh build's own first Settling entry) or after one already has
    /// (a follow-up run dispatched onto an already-open PR).
    /// </param>
    /// <param name="rebaseWasRecovered">
    /// True when the rebase that preceded this gate failure needed the recovery session's own
    /// judgment rather than applying cleanly on its own — named in the prompt so the session knows
    /// a conflict was resolved by hand here, not just replayed mechanically, which is one more
    /// place a subtle regression could hide.
    /// </param>
    /// <param name="humanGuidance">
    /// Set only on the one repair round a human's own <c>h9k review resolve --needs-fixes</c> buys
    /// after the round cap is spent (<see cref="ReviewEngine"/>'s dispatch from
    /// <see cref="ReviewPhase.SettlingGateRepairNeeded"/>): their guidance, inserted so the agent
    /// applies it rather than repeating whatever the earlier round(s) already tried.
    /// </param>
    public static string BuildSettlingGateRepair(
        TaskDetails task, ProjectDetails project, string branch, CommitStyle commitStyle,
        string? pullRequestUrl, string baseBranch, string rebasedFromCommit, string rebasedOntoCommit,
        bool rebaseWasRecovered, string gateOutput, string? humanGuidance = null, TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/settling-gate-repair.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        if (pullRequestUrl.IsNotBlank())
        {
            prompt.AppendLine($"Pull request: {pullRequestUrl}");
            prompt.AppendLine();
        }

        prompt.AppendLine(Fragment(
            file, rebaseWasRecovered ? "rebase-summary-recovered" : "rebase-summary-clean",
            ("FromCommit", ShortCommit(rebasedFromCommit)), ("OntoCommit", ShortCommit(rebasedOntoCommit))));
        AppendFragment(prompt, file, "gate-failed-intro");
        prompt.AppendLine();

        if (humanGuidance.IsNotBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "human-guidance-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "human-guidance-intro");
            prompt.AppendLine();
            prompt.AppendLine(humanGuidance);
            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "gate-output-heading"));
        prompt.AppendLine();
        prompt.AppendLine("```");
        prompt.AppendLine(gateOutput);
        prompt.AppendLine("```");
        prompt.AppendLine();

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        AppendProjectHome(prompt, project);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "worktree-lead"));
        prompt.AppendLine(pullRequestUrl.IsNotBlank()
            ? Fragment(file, "worktree-with-pr", ("Branch", branch))
            : Fragment(file, "worktree-without-pr", ("Branch", branch)));
        AppendFragment(prompt, file, "reproduce-and-fold");
        AppendCommitStyleRules(prompt, commitStyle, baseBranch);
        AppendFragment(prompt, file, "no-need-to-run-gate");
        if (pullRequestUrl.IsNotBlank())
        {
            AppendFragment(prompt, file, "no-push-with-pr");
        }
        else
        {
            AppendFragment(prompt, file, "no-push-without-pr");
        }

        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary");
        AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    private static string ShortCommit(string commit) => commit.Length > 10 ? commit[..10] : commit;

    /// <summary>
    /// The stacked-replay variant (task: a stacked pull-request edge exists as an explicit opt-in
    /// dependency): the child's parent branch moved — it merged and this pull request has already
    /// been retargeted onto <paramref name="baseBranch"/>, or it was force-pushed — and this
    /// session replays the child's own commits onto the new head.
    /// <para>
    /// Mechanical, and the prompt says so in the strongest terms it can: one <c>git rebase --onto</c>
    /// with a named upstream, the gates, nothing else. No review cycle reads the result
    /// (<c>ReviewStageComposition.None</c> is forced for this run), which is exactly why the prompt
    /// must not leave room for judgment: a session that "improved something while it was in there"
    /// would put unreviewed intent on a branch nothing is going to read. A conflict is the one
    /// thing it may resolve, and only by reading both sides' intent the way
    /// <see cref="BuildRebase"/> teaches — because git cannot replay a conflicting patch without a
    /// decision, and stopping instead would leave the branch mid-rebase.
    /// </para>
    /// <para>
    /// Both commits are named literally rather than as refs, and both are the caller's own observed
    /// values. <paramref name="upstreamCommit"/> is the boundary: everything at or before it belongs
    /// to the parent (or to the base a previous replay put this branch on) and must NOT be replayed,
    /// since <paramref name="ontoCommit"/> already holds that work. Getting it wrong in either
    /// direction is the whole hazard — too low replays the parent's commits as duplicates, too high
    /// drops the child's own work — which is why the prompt asks for the count to be checked before
    /// and after. <paramref name="ontoCommit"/> is where the replay lands, a commit rather than
    /// <c>origin/&lt;base&gt;</c> so the session lands exactly where closeout looked rather than
    /// wherever that ref drifted to while this run waited to be claimed; the fetch below still
    /// happens, because the commit has to be present locally before it can be rebased onto.
    /// </para>
    /// </summary>
    /// <param name="commitStyle">
    /// The project's own resolved style (project-over-platform, Decisions Log #26), threaded in
    /// exactly as it is for every sibling follow-up prompt rather than assumed. A replay only ever
    /// reads it for the gate-fix instruction below, and that instruction is where assuming would
    /// bite hardest: an append-style project told the narrative rule would have an unreviewed
    /// mechanical session fold a fix into an owning commit and rewrite this branch's history
    /// against the convention the project declared (conformance review, cycle 6 — this used to
    /// hard-code <c>Narrative</c> while <see cref="RunLauncher"/> held the resolved value at the
    /// call site).
    /// </param>
    public static string BuildStackReplay(
        TaskDetails task, ProjectDetails project, string branch, string pullRequestUrl,
        CommitStyle commitStyle, string baseBranch, string upstreamCommit, string ontoCommit,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/stack-replay.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine($"Pull request: {pullRequestUrl}");
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro", ("BaseBranch", baseBranch));
        prompt.AppendLine();
        AppendFragment(prompt, file, "no-new-intent");
        prompt.AppendLine();

        if (task.FollowUpReason.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "follow-up-reason", ("Reason", task.FollowUpReason)));
            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        WorkPromptBuilder.AppendProjectHome(prompt, project);

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("Branch", branch));
        AppendRetainedWorktreeNote(prompt);
        prompt.AppendLine(PromptTemplates.Load(file, "replay-shape"));
        prompt.AppendLine(PromptTemplates.Load(file, "fetch-both-commits"));
        AppendFragment(prompt, file, "record-what-replays", ("UpstreamCommit", upstreamCommit));
        prompt.AppendLine(Fragment(
            file, "rebase-onto-command",
            ("OntoCommit", ontoCommit), ("UpstreamCommit", upstreamCommit), ("Branch", branch)));
        AppendFragment(
            prompt, file, "boundary-explanation",
            ("UpstreamCommit", upstreamCommit), ("OntoCommit", ontoCommit), ("BaseBranch", baseBranch));
        AppendFragment(prompt, file, "resolve-conflicts");
        AppendFragment(prompt, file, "check-replay");
        // The fold's own boundary is where this replay just landed, not `origin/<base>`: after the
        // replay this branch's own commits are exactly the ones after `ontoCommit`, and on the
        // force-pushed-parent trigger that ref is the parent's branch, which can move again while
        // this session runs — the same reason the replay itself is keyed to a commit rather than a
        // ref (independent pre-PR review, cycle 2, adversarial lens).
        AppendRebaseVerificationRule(
            prompt, project, commitStyle, baseBranch,
            fold: WorkPromptBuilder.StackedForkPoint(project, baseBranch, ontoCommit) is null
                ? null
                : new FoldBoundary(
                    ontoCommit,
                    Fragment(file, "fold-reason", ("BaseBranch", baseBranch)).Split('\n')));
        AppendFragment(prompt, file, "no-push");
        AppendFragment(prompt, file, "stop-if-blocked");
        WorkPromptBuilder.AppendSessionEndsAtFinalMessageRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        WorkPromptBuilder.AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "closing-summary");
        WorkPromptBuilder.AppendHandoffRules(prompt);

        return prompt.ToString();
    }

    /// <summary>
    /// Who wrote what, and why the answer is not "read the login" (Decisions Log #62).
    /// <para>
    /// The discriminator this section teaches works only because agents author commits and
    /// comments as the human and never open review threads of their own. Origin incident
    /// (2026-08-20): Brian left a review comment on PR #20, and the machinery was
    /// structurally blind to it — the closeout inspector counted only Copilot-authored
    /// threads, and agent replies posted under his own login made human and agent comments
    /// indistinguishable by author. The thread-STARTER rule is what survives that, and it
    /// survives only while the invariant holds; AGENTS.md records it beside the
    /// no-bot-identity rule for that reason.
    /// </para>
    /// </summary>
    private static void AppendReviewerAttributionRules(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/reviewer-attribution.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "rules");
        prompt.AppendLine();
        AppendFragment(prompt, file, "pending");
        prompt.AppendLine();
    }

    /// <summary>
    /// The header line a triage block opens with, and the tags <c>ReviewResultParser.ParseThreadDispositions</c>
    /// reads off it (task: every review thread on a pull request gets a triage disposition before
    /// any fix work). Public so a test can build a summary against the exact contract this prompt
    /// teaches, rather than a copy of it that could drift.
    /// </summary>
    public const string ThreadDispositionMarker = ReviewResultParser.ThreadDispositionMarker;

    /// <summary>The line that closes the last triage block, taught below so recap and dispute prose is never misread as a thread's own reasoning.</summary>
    public const string ThreadDispositionSummaryMarker = ReviewResultParser.ThreadDispositionSummaryMarker;

    /// <summary>
    /// The triage gate itself (task: every review thread on a pull request gets a triage
    /// disposition before any fix work — origin: two full fix laps in two days bought by false
    /// Copilot threads on PR #199 and PR #229, both resolved by hand on Brian's word after
    /// evidence). Every thread gets exactly one of three dispositions before any code changes, and
    /// the marker contract this section teaches is what lets <c>RunSupervisor</c> record each one
    /// on the run stream (<c>ReviewThreadsTriaged</c>) so the decline rate is measurable rather
    /// than an impression. <see cref="AppendThreadHandlingRules"/> teaches what each disposition
    /// means for the reply and the resolve — this section teaches only the gate and the marker.
    /// </summary>
    private static void AppendThreadTriageRules(StringBuilder prompt, string projectName)
    {
        const string file = $"{TemplateDirectory}/thread-triage.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "fix-disposition");
        AppendFragment(prompt, file, "decline-disposition");
        AppendFragment(prompt, file, "route-disposition", ("ProjectName", projectName));
        prompt.AppendLine();
        AppendFragment(prompt, file, "no-touch-until-triaged");
        prompt.AppendLine();
        AppendFragment(prompt, file, "close-with-blocks");
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "block-shape",
            ("ThreadDispositionMarker", ThreadDispositionMarker),
            ("ThreadIdPlaceholder", ReviewResultParser.ThreadIdPlaceholder));
        prompt.AppendLine();
        // Split rather than substituted whole, to match main's own accidental mid-word line
        // wrap exactly (an existing test asserts the first half as its own substring) while
        // keeping the full contract literal out of the template file itself.
        string threadIdPlaceholder = ReviewResultParser.ThreadIdPlaceholder;
        int placeholderSplitAt = threadIdPlaceholder.LastIndexOf(' ');
        prompt.AppendLine(Fragment(file, "no-placeholder-echo-part1", ("Part1", threadIdPlaceholder[..placeholderSplitAt])));
        prompt.AppendLine(Fragment(file, "no-placeholder-echo-part2", ("Part2", threadIdPlaceholder[(placeholderSplitAt + 1)..])));
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "block-ordering", ("ThreadDispositionSummaryMarker", ThreadDispositionSummaryMarker));
        AppendFragment(prompt, file, "kind-classification");
        prompt.AppendLine();
    }

    /// <summary>
    /// How a triage disposition becomes a reply and a resolve decision (Decisions Log #62, #159).
    /// A fix invites no argument and is replied and resolved the same way regardless of who
    /// started the thread; a decline or a route is different — the evidence or the routing note
    /// still goes in the thread, but only a bot-authored thread may be resolved afterward. A
    /// human-authored one stays open: posting evidence answers the reviewer, closing their thread
    /// for them does not (task: every review thread on a pull request gets a triage disposition
    /// before any fix work — "agents never close a human's thread" is the acceptance bar this
    /// asymmetry exists to meet). Bounded on purpose: one honest attempt per thread per follow-up,
    /// the never-loop rule the review park already runs on.
    /// <para>
    /// One thread is not answered here at all (Decisions Log #152, and the identical carve-out at
    /// the top of the resolve-review-threads skill): a human reviewer whose own
    /// <c>CHANGES_REQUESTED</c> verdict still stands. A disagreement with a standing review is
    /// never posted by an agent — it is drafted, parked, and sent, edited or dropped by the
    /// implementer. This lap reaches such a thread whenever the changes-requested fix lap
    /// (<see cref="BuildReviewRequestedChanges"/>) already pushed and left the disputed thread
    /// unresolved, and the next closeout sweep dispatched an ordinary <c>ReviewFeedback</c>
    /// follow-up over it: without the carve-out stated HERE, this prompt's own decline rule told
    /// that session to post its evidence into the thread of the person the implementer may have
    /// deliberately left unanswered, which is the one act #152 exists to prevent. The skill states
    /// it too, but a skill is a load away and the prompt is the last word (routed finding, run
    /// 01a07d98, adversarial lens, cycle 3). #159 is untouched: a human's plain thread comment,
    /// with no standing changes-requested verdict behind it, still gets the evidence-based decline
    /// reply and still stays open for them to resolve.
    /// </para>
    /// </summary>
    private static void AppendThreadHandlingRules(StringBuilder prompt, ProjectDetails project)
    {
        const string file = $"{TemplateDirectory}/thread-handling.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "fix-rule");
        AppendFragment(prompt, file, "decline-rule-lead");
        AppendFragment(
            prompt, file, "decline-rule-markers",
            ("DisagreementMarker", ReviewResultParser.DisagreementMarker),
            ("ReviewerAskedMarker", ReviewResultParser.ReviewerAskedMarker),
            ("DisagreementReasoningMarker", ReviewResultParser.DisagreementReasoningMarker),
            ("ProposedReplyMarker", ReviewResultParser.ProposedReplyMarker));
        AppendFragment(prompt, file, "route-rule");
        AppendFragment(prompt, file, "question-rule");
        AppendFragment(prompt, file, "never-resolve-without-reply");
        AppendFragment(prompt, file, "one-attempt");
        prompt.AppendLine();
        AppendFragment(prompt, file, "body-comment");
        prompt.AppendLine();
        AppendWritingConventions(
            prompt, string.Empty, project.WritingConventions, PromptTemplates.Load(file, "writing-conventions-lead-in"));
        prompt.AppendLine();
    }

    /// <summary>
    /// The data-only boundary applied to review threads (Decisions Log #62), the same fence
    /// this file already puts around an adopted issue body and around blocker context. Widening
    /// the follow-up from "Copilot's threads" to "every thread, and a person's gets more care"
    /// widened the attack surface with it: the agent is now told to weigh the text of anyone who
    /// can comment on the pull request, and that text arrives from GitHub at run time rather
    /// than through a section this prompt fences.
    /// <para>
    /// So the fence has to be a standing rule, and it lives in the working rules for the reason
    /// <see cref="Hall9k.Connectors.Prompts.WorkPromptBuilder.AppendAdoptedContextRule"/> gives: the daemon authors every line of that
    /// section and it is the last word in the prompt. Scoped rather than blanket, because a
    /// review thread legitimately asks for things — changing code, explaining a choice,
    /// resolving the thread — and a rule that read all of it as inert would break the job. What
    /// it refuses is a thread reaching past the review to the platform's own rules: push this
    /// yourself, skip the gates, go work in another repository.
    /// </para>
    /// </summary>
    private static void AppendThreadTextBoundaryRule(StringBuilder prompt) =>
        AppendFragment(prompt, $"{TemplateDirectory}/thread-text-boundary.md", "rule");

    /// <summary>
    /// The park (Decisions Log #62): the never-loop rule applies to a human's thread exactly
    /// as it does to a review finding, so a disagreement the agent cannot honestly judge goes
    /// to a human with both positions recorded. RunSupervisor reads the marker this section
    /// asks for and parks the run rather than pushing.
    /// <para>
    /// It is also where <see cref="AppendThreadHandlingRules"/>'s standing-review carve-out lands
    /// (Decisions Log #152), which is why the gate below is not "undecidable" alone: that
    /// disagreement may be one the session could answer with evidence, and it is withheld anyway
    /// because sending it is the implementer's act. The park itself is the ordinary thread-dispute
    /// park — <c>RunSupervisor</c> appends <c>ReviewDisagreementParked</c>, and with it the three
    /// <c>h9k review resolve</c> posting choices, only for a
    /// <see cref="FollowUpKind.ReviewRequestedChanges"/> lap — so this prompt names no posting flag
    /// a human running <c>h9k review resolve</c> here would be refused: the drafted reply is in the
    /// dispute file the park's reason points at, and they post it themselves.
    /// </para>
    /// </summary>
    private static void AppendThreadDisputeRules(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/thread-dispute.md";
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "close-with-dispute-marker", ("DisputeMarker", DisputeMarker));
        AppendFragment(
            prompt, file, "record-both-positions",
            ("ThreadDispositionSummaryMarker", ThreadDispositionSummaryMarker),
            ("ProposedReplyMarker", ReviewResultParser.ProposedReplyMarker));
        AppendFragment(prompt, file, "platform-parks");
        prompt.AppendLine();
        AppendFragment(prompt, file, "resolved-line", ("ResolvedMarker", ResolvedMarker));
        prompt.AppendLine();
    }

    /// <summary>
    /// Follow-up runs reuse the previous run's retained worktree (Decisions Log #21),
    /// which by design may carry uncommitted stranded work — a prior session's finished
    /// but never-committed changes (the retained-worktree resume exists exactly so that
    /// work survives). The agent must look before it leaps.
    /// </summary>
    private static void AppendRetainedWorktreeNote(StringBuilder prompt) =>
        AppendFragment(prompt, $"{TemplateDirectory}/retained-worktree.md", "note");

    /// <summary>
    /// How a follow-up's fixes land on the PR branch (Decisions Log #26). Narrative
    /// enforces the AGENTS.md authored-history rule: fixups mapped by file ownership,
    /// autosquash onto the base, and a tree-identity check so the verification-gate
    /// results honestly describe the rebased tree the platform will force-push. Append
    /// keeps the historic stack-on-top behavior. Both end the same way: the agent never
    /// pushes; the platform does, with --force-with-lease for follow-up runs.
    /// </summary>
    /// <param name="fold">
    /// Where the fold rebases from when <paramref name="baseBranch"/> is a branch this one is
    /// stacked on rather than the project's own — see <see cref="FoldBoundary"/>. Nothing in this
    /// prompt moves the branch, so the recorded fork point is still this branch's own boundary
    /// here, unlike in a rebase prompt. Null for every ordinary run.
    /// </param>
    private static void AppendCommitStyleRules(
        StringBuilder prompt, CommitStyle commitStyle, string baseBranch, FoldBoundary? fold = null)
    {
        const string file = $"{TemplateDirectory}/commit-style.md";
        if (commitStyle == CommitStyle.Append)
        {
            AppendFragment(prompt, file, "append-style");
            return;
        }

        AppendFragment(prompt, file, "narrative-lead");
        if (fold is not null)
        {
            // That skill folds against `origin/<base>`, which is the one thing a stacked child may
            // not do — the pointer is qualified rather than left to contradict the boundary named
            // below (independent pre-PR review, cycle 2, adversarial lens).
            AppendFragment(prompt, file, "stacked-skill-caveat", ("BaseBranch", baseBranch));
        }

        AppendFragment(prompt, file, "map-and-land-fixups");
        prompt.AppendLine(Fragment(file, "fold-command", ("Argument", fold?.Argument ?? $"origin/{baseBranch}")));
        foreach (string line in fold?.Reason ?? [])
        {
            prompt.AppendLine(line);
        }

        AppendFragment(prompt, file, "tree-identity-and-push");
    }

    /// <summary>
    /// The independent pre-PR review (Decisions Log #23), one pass per lens (log #59): a
    /// fresh session that never saw the implementation reasoning reads the branch's diff
    /// against the base before any pull request exists. Verified findings only, and a
    /// machine-readable verdict on the last line — the daemon parses it and merges the
    /// cycle's verdicts into one.
    /// <para>
    /// A lens the platform does not recognize — including the blank lens of a run dispatched
    /// before lenses existed — gets the conformance prompt, because a single reviewer with no
    /// stated lens is exactly what the conformance pass has always been.
    /// </para>
    /// </summary>
    /// <param name="priorRulings">
    /// The task's settled human rulings on earlier review parks (task: review prompts carry
    /// prior rulings), oldest first; null or empty when the task has never parked. Handed to
    /// BOTH lenses: the adversarial lens stays blind to the task's objective and acceptance
    /// criteria, but a settled park ruling is not that withheld information — it exists solely so
    /// neither lens re-raises a question a human already answered.
    /// </param>
    /// <param name="sinceSha">
    /// Read when <paramref name="mode"/> is <see cref="ReviewMode.FinalFullPass"/> (task: the
    /// mandatory FinalFullPass rereads only the commits no full-scope pass has already read): the
    /// worktree HEAD of the last full-scope cycle that read this branch. Also read when
    /// <paramref name="mode"/> is <see cref="ReviewMode.Discovery"/> and this is a ReviewFeedback or
    /// FailingChecks follow-up's own opening cycle (task: a lap reviews only what it changed): the
    /// pull request head the previous run pushed. Either way, null when none is on record or it
    /// could not be resolved — in which case the prompt falls back to the full base-branch diff
    /// instruction rather than guessing at a boundary. Ignored for every other mode, and for an
    /// ordinary Discovery dispatch, both of which always read the full diff.
    /// </param>
    /// <param name="interactiveModeEnabledOverride">
    /// A freshly-read replacement for <c>task.InteractiveModeEnabled</c> (independent pre-PR
    /// review, cycle 1, adversarial lens): null falls back to the snapshot on <paramref
    /// name="task"/> itself, which is what every caller holding a short-lived task read (a
    /// fresh dispatch, a test) still wants — only a caller holding <paramref name="task"/>
    /// across a session that can run for hours, and so risks it going stale mid-flight (a
    /// review pass's own dispatch out of <c>ReviewEngine</c>), passes a value here instead.
    /// </param>
    public static string BuildReview(
        TaskDetails task, ProjectDetails project, string branch, int cycle, ReviewLens lens,
        ReviewMode? mode = null,
        IReadOnlyList<ReviewParkResolution>? priorRulings = null,
        IReadOnlyList<ExternalInteractionRecord>? priorHumanDirectedInteractions = null,
        ReviewMechanicsOverride? mechanicsOverride = null,
        string? sinceSha = null,
        IReadOnlyList<BoundaryApprovalRecord>? priorBoundaryApprovals = null,
        string? interactiveSessionAddress = null,
        bool? interactiveModeEnabledOverride = null,
        IReadOnlyList<HumanFixRecord>? priorHumanFixes = null,
        TimeSpan? commandTimeout = null)
    {
        bool interactiveModeEnabled = interactiveModeEnabledOverride ?? task.InteractiveModeEnabled;
        return lens == ReviewLens.Adversarial
            ? BuildAdversarialReview(
                task.Id, project, branch, cycle, mode ?? ReviewMode.Discovery, priorRulings,
                priorHumanDirectedInteractions, mechanicsOverride, sinceSha, priorBoundaryApprovals,
                interactiveModeEnabled, interactiveSessionAddress, priorHumanFixes, commandTimeout)
            : BuildConformanceReview(
                task, project, branch, cycle, mode ?? ReviewMode.Discovery, priorRulings,
                priorHumanDirectedInteractions, mechanicsOverride, sinceSha, priorBoundaryApprovals,
                interactiveSessionAddress, interactiveModeEnabled, priorHumanFixes, commandTimeout);
    }

    /// <summary>
    /// A pr-review task's one-shot lens (PrReviewEngine): delegates to <see cref="BuildReview"/>
    /// whole — same finding/verdict contract, same read-only mechanics — and appends only what
    /// genuinely differs about reviewing someone else's already-open pull request rather than this
    /// task's own implementation: there is nothing here to fix or commit, and the conformance basis
    /// is the pull request's own title/description plus whatever issue or Jira card it references,
    /// imported at task creation — often thinner than a task's own acceptance criteria, so a thin
    /// basis is graded as context for the human rather than as a blocking defect. Always cycle 1: a
    /// pr-review run never re-reviews, so there is no second cycle to number.
    /// <para>
    /// <paramref name="baseBranch"/> is the pull request's own base ref, never
    /// <c>project.BaseBranch</c>: the two disagree whenever the reviewed pull request targets
    /// anything other than the project's default branch, and the mechanics section must name the
    /// range it can actually reproduce. The checkout is a detached, branch-less worktree
    /// (<c>CreatePrReviewCheckoutAsync</c>), so the mechanics section also says that plainly rather
    /// than naming a `pr/&lt;n&gt;` ref that does not exist. And no verification ever runs against a
    /// foreign pull request — the gate status here says so, rather than asserting an observation
    /// nobody made.
    /// </para>
    /// </summary>
    public static string BuildPrReviewLens(
        TaskDetails task, ProjectDetails project, string branch, ReviewLens lens, string baseBranch,
        TimeSpan? commandTimeout = null)
    {
        // A pr-review task retries through the same TaskDecider.Retry every other task type
        // does — nothing gates it to TaskType.PrReview — so an operator's `h9k task retry
        // --reason` on a stalled or unclear pr-review dispatch is just as live an instruction
        // here as it is on a build follow-up. Appended after the pr-review framing paragraph
        // rather than woven into it: unlike WorkPromptBuilder.Build and the follow-up builders
        // above, this prompt's own "# Independent review" heading and every section under it
        // come from the shared BuildReview/BuildConformanceReview/BuildAdversarialReview
        // internals, so there is no earlier point in this method's own text to insert a "##"
        // heading without either duplicating those internals or reordering headings the shared
        // review prompt does not expect (independent pre-PR review, cycle 3, conformance lens).
        // Given to both lenses, not gated to Conformance alone: the adversarial lens's own
        // blindness to the task's objective and acceptance criteria (BuildReview's own doc)
        // does not cover an operator's retry-time instruction about the review itself — the
        // same reasoning a settled park ruling already gets handed to both lenses for.
        const string file = $"{TemplateDirectory}/pr-review-lens.md";
        StringBuilder guidance = new();
        AppendOperatorGuidanceSection(guidance, task);

        return BuildReview(
            task, project, branch, cycle: 1, lens, priorRulings: null,
            mechanicsOverride: new ReviewMechanicsOverride(
                baseBranch,
                CheckoutDescription: PromptTemplates.Load(file, "checkout-description"),
                GatesObserved: false,
                DiffIsForeignPullRequest: true),
            commandTimeout: commandTimeout)
            + "\n\n" + PromptTemplates.Load(file, "foreign-pr-notice")
            + (lens == ReviewLens.Conformance ? PromptTemplates.Load(file, "conformance-basis-addendum") : string.Empty)
            + (guidance.Length > 0 ? "\n\n" + guidance : string.Empty);
    }

    /// <summary>
    /// What <see cref="AppendReviewMechanics"/> needs overridden when the range under review is not
    /// this task's own diff against the project's own base branch. Two callers, and only the first
    /// changes anything beyond the base branch: <see cref="BuildPrReviewLens"/>, where the whole
    /// diff belongs to someone else's pull request, and a stacked child's ordinary pre-PR loop,
    /// where the diff is this task's own but the base it is a delta against is the parent's branch
    /// rather than the project's (task: a stacked pull-request edge exists as an explicit opt-in
    /// dependency) — that one passes <see cref="BaseBranch"/> and <see cref="ForkPointCommit"/> and
    /// takes every other default,
    /// which is what makes its reviewers see the child's own delta rather than the parent's work
    /// alongside it. Null everywhere else, so an unstacked pre-PR loop keeps reading
    /// <c>project.BaseBranch</c>, the real `on branch` wording, and the real gate-status
    /// observation exactly as it always has.
    /// </summary>
    /// <param name="ForkPointCommit">
    /// The commit this branch was actually cut from (<c>RunDispatched.BaseCommit</c>), named as the
    /// boundary of every range this pass reads and scopes against in place of
    /// <c>origin/{BaseBranch}</c>. Set only for a stacked child, and only when its run recorded
    /// one: its base is another task's branch, and an ordinary review lap on that branch
    /// force-pushes <c>origin/&lt;parent&gt;</c> past the point this child was cut from — which
    /// collapses the merge base a three-dot range resolves and folds the parent's whole
    /// rewritten-away delta into what the reviewer reads, and scopes, as this child's own work
    /// (conformance and adversarial review, cycle 4). The same hazard the build session's
    /// self-review range, the recompose reset, the rebase replay and both fixup-fold instructions
    /// are already keyed to a commit for — see <c>WorkPromptBuilder.StackedForkPoint</c>. Null
    /// everywhere else, including a stacked child whose run recorded no fork point, which falls
    /// back to the parent branch's ref rather than inventing a boundary.
    /// </param>
    /// <param name="CheckoutDescription">
    /// The first mechanics line, replacing the ordinary "you are in the implementation's git
    /// worktree on branch X". Null keeps that ordinary wording, which is right for a stacked child:
    /// it really is in its own worktree on its own branch — only its base differs.
    /// </param>
    /// <param name="GatesObserved">
    /// Whether this run's own gates actually ran, which is what the gate-status section is allowed
    /// to assert. True for every ordinary pre-PR pass including a stacked child's; false only for a
    /// foreign pull request, where nothing was verified locally at all.
    /// </param>
    /// <param name="DiffIsForeignPullRequest">
    /// True only for <see cref="BuildPrReviewLens"/> (cycle-1 conformance and adversarial
    /// findings): the diff under review belongs to another contributor's already-open pull
    /// request rather than this task's own implementation, which changes more than the mechanics
    /// section above states. Gates three things every other caller keeps as-is: this task's own
    /// acceptance criteria are never the standard the diff is judged against (they describe the
    /// review deliverable, not the foreign diff — <see cref="BuildConformanceReview"/>); the
    /// checkout's own AGENTS.md/CLAUDE.md is the pull request author's file, not this project's
    /// settled doctrine, and a diff can edit it in the same commit it wants excused —
    /// <see cref="AppendSettledRulings"/>; and the two lenses are dispatched one after another by
    /// <c>PrReviewEngine</c> rather than concurrently, so there is no second pass sharing this
    /// worktree's <c>obj/</c>/<c>bin/</c> at the same time — <see cref="AppendReviewMechanics"/>.
    /// </param>
    public sealed record ReviewMechanicsOverride(
        string BaseBranch, string? ForkPointCommit = null, string? CheckoutDescription = null,
        bool GatesObserved = true, bool DiffIsForeignPullRequest = false);

    /// <summary>
    /// The one reviewer a <see cref="ReviewMode.Verify"/> cycle dispatches (task: review cycles
    /// after the first, origin: 576M input tokens in one day re-reading 12k-line diffs with two
    /// Opus lenses to judge 40-line fixes). Discovery already happened at cycle 1 — every still-
    /// active track's own findings, from the cycle this cycle is verifying, ride in below — so this
    /// pass is spec-aware by design: verify each fix actually landed and check its blast radius,
    /// rather than re-deriving the whole diff from a blank slate. It answers for every track named
    /// in <paramref name="tracks"/> at once, tagging each finding with which one it belongs to.
    /// This is also the one review lens that reads a fix session's own closing summary
    /// (<paramref name="priorFixPosition"/>), so it is the one place that instructs a reviewer to
    /// grade evidence of host-load flake reproduction there as a high-severity conformance finding
    /// (Decisions Log §16 #169) — <see cref="WorkPromptBuilder.AppendNoHostLoadForFlakeReproductionRule"/>
    /// tells a session not to generate that load; this tells the reviewer what to do if one did anyway.
    /// </summary>
    /// <param name="tracks">The still-active tracks this pass stands in for.</param>
    /// <param name="priorFindings">The prior cycle's own merged findings document, verbatim.</param>
    /// <param name="priorFixPosition">The fix session's own closing summary for that cycle, verbatim.</param>
    /// <param name="sinceSha">
    /// The worktree HEAD as of the prior cycle's own dispatch, or null when it could not be pinned
    /// down — in which case the prompt falls back to a full base-branch diff instruction rather than
    /// guessing at a boundary.
    /// </param>
    /// <param name="priorCycleMode">
    /// The shape the cycle whose findings are quoted below actually took (cycle-4 conformance
    /// finding): the prompt cannot honestly claim "two reviewers read this branch in full" when that
    /// cycle was itself a delta-scoped <see cref="ReviewMode.Verify"/> pass rather than a
    /// <see cref="ReviewMode.Discovery"/> or <see cref="ReviewMode.FinalFullPass"/> cycle — a false
    /// completeness claim is exactly the kind of unobserved fact AGENTS.md says never to assert.
    /// </param>
    /// <param name="priorCycleSinceSha">
    /// That same cycle's own recorded <see cref="Events.ReviewDispatched.SinceSha"/> (independent
    /// pre-PR review, cycle 1 adversarial finding): a <see cref="ReviewMode.FinalFullPass"/> cycle no
    /// longer guarantees a full-branch read on its own (Decisions Log #115), and neither does a
    /// <see cref="ReviewMode.Discovery"/> cycle that was itself a ReviewFeedback or FailingChecks
    /// follow-up's own scoped opening lap (task: a lap reviews only what it changed) — a non-null
    /// value here means that cycle was itself scoped rather than a full-branch read, and the same
    /// false-completeness problem <paramref name="priorCycleMode"/> guards against applies just as
    /// much to a scoped FinalFullPass or a scoped opening Discovery as it does to a Verify pass.
    /// </param>
    /// <param name="baseCommit">
    /// This run's own recorded fork point (<c>RunDispatched.BaseCommit</c>), which is the boundary
    /// the full-diff fallback below names on a stacked child instead of <c>origin/&lt;parent&gt;</c>
    /// — see <see cref="ReviewMechanicsOverride.ForkPointCommit"/> for the force-pushed-parent
    /// hazard. Ignored for every ordinary run, whose prompt is unchanged.
    /// </param>
    public static string BuildReviewVerify(
        TaskDetails task, ProjectDetails project, string branch, int cycle, IReadOnlyList<ReviewLens> tracks,
        string priorFindings, string priorFixPosition, string? sinceSha, ReviewMode priorCycleMode,
        string? priorCycleSinceSha, IReadOnlyList<ReviewParkResolution>? priorRulings = null,
        IReadOnlyList<ExternalInteractionRecord>? priorHumanDirectedInteractions = null,
        IReadOnlyList<BoundaryApprovalRecord>? priorBoundaryApprovals = null,
        string? interactiveSessionAddress = null,
        bool? interactiveModeEnabledOverride = null,
        string? baseBranch = null,
        string? baseCommit = null,
        IReadOnlyList<HumanFixRecord>? priorHumanFixes = null,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/review-verify.md";
        bool interactiveModeEnabled = interactiveModeEnabledOverride ?? task.InteractiveModeEnabled;
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        // The full-diff fallback below only fires when the prior cycle's own tip could not be
        // pinned down, but when it does it must name the same boundary every other stacked
        // instruction names: a commit, not the parent's ref, which a force-push moves out from
        // under the range (WorkPromptBuilder.StackedForkPoint; conformance review, cycle 4).
        string? fullDiffForkPoint = WorkPromptBuilder.StackedForkPoint(project, effectiveBaseBranch, baseCommit);
        string fullDiffBoundary = fullDiffForkPoint ?? $"origin/{effectiveBaseBranch}";
        bool priorCycleReadFullBranch =
            (priorCycleMode != ReviewMode.FinalFullPass && priorCycleMode != ReviewMode.Discovery)
            || priorCycleSinceSha is null;
        string priorCycleDescription = priorCycleMode == ReviewMode.Verify
            ? PromptTemplates.Load(file, "prior-cycle-verify")
            : priorCycleReadFullBranch
                ? PromptTemplates.Load(file, "prior-cycle-full")
                : PromptTemplates.Load(file, "prior-cycle-partial");
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro", ("PriorCycleDescription", priorCycleDescription));
        prompt.AppendLine();
        if (tracks.Count > 1)
        {
            AppendFragment(prompt, file, "tracks-both");
        }
        else
        {
            AppendFragment(prompt, file, "tracks-single");
        }
        foreach (ReviewLens track in tracks)
        {
            prompt.AppendLine(track == ReviewLens.Adversarial
                ? PromptTemplates.Load(file, "track-adversarial-line")
                : PromptTemplates.Load(file, "track-conformance-line"));
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "what-diff-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "acceptance-criteria-heading"));
        foreach (string criterion in task.AcceptanceCriteria)
        {
            prompt.AppendLine($"- {criterion}");
        }

        prompt.AppendLine();
        AppendSettledRulings(
            prompt, priorRulings, priorHumanDirectedInteractions,
            priorBoundaryApprovals: priorBoundaryApprovals, priorHumanFixes: priorHumanFixes);
        prompt.AppendLine(PromptTemplates.Load(file, "prior-findings-heading"));
        prompt.AppendLine();
        if (priorFindings.IsBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "no-prior-findings"));
        }
        else
        {
            AppendFragment(prompt, file, "prior-findings-quoted-intro");
            prompt.AppendLine();
            prompt.AppendLine(QuoteAsHistory(priorFindings));
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "fix-session-heading"));
        prompt.AppendLine();
        if (priorFixPosition.IsBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "no-fix-session-summary"));
        }
        else
        {
            AppendFragment(prompt, file, "fix-session-quoted-intro");
            prompt.AppendLine();
            prompt.AppendLine(QuoteAsHistory(priorFixPosition));
        }

        prompt.AppendLine();
        AppendFragment(prompt, file, "host-load-warning");

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "how-to-review-heading"));
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "worktree-branch", ("Branch", branch)));
        prompt.AppendLine(sinceSha is { } sha
            ? Fragment(file, "diff-with-sha", ("Sha", sha))
            : fullDiffForkPoint is null
                ? Fragment(file, "diff-without-sha-no-fork", ("FullDiffBoundary", fullDiffBoundary))
                : Fragment(
                    file, "diff-without-sha-with-fork",
                    ("FullDiffBoundary", fullDiffBoundary), ("EffectiveBaseBranch", effectiveBaseBranch)));
        AppendFragment(prompt, file, "review-checklist", ("NeedsFixesWord", "needs-fixes"));
        AppendFragment(prompt, file, "no-build-note");
        AppendForegroundGatesRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: false);
        AppendReviewGateStatus(prompt, project);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        if (interactiveModeEnabled)
        {
            AppendOutboundMilestoneRules(
                prompt, "review", OutboundMilestone.Review, interactiveSessionAddress,
                verdictBoundaryChoicesTaskId: task.Id);
        }

        // The scope rule decides in-scope from out-of-scope, so it has to name the same boundary
        // the range above does rather than the project's own base — this pass is base-aware and its
        // finding contract was still reading project.BaseBranch, which on a stacked child tags the
        // parent's already-reviewed lines as this pull request's own work (class sweep, conformance
        // review cycle 4). The override is used purely as the carrier for that base pair; every
        // other mechanic it can change stays at its default.
        AppendFindingContract(
            prompt, project, ReviewMode.Verify,
            effectiveBaseBranch == project.BaseBranch
                ? null
                : new ReviewMechanicsOverride(effectiveBaseBranch, ForkPointCommit: fullDiffForkPoint));
        AppendVerifyTrackTagContract(prompt, tracks);
        AppendVerdictContract(prompt, cycle, ReviewMode.Verify);
        prompt.AppendLine();
        AppendFragment(prompt, file, "verdict-outcome-tail");

        return prompt.ToString();
    }

    /// <summary>
    /// Quotes a verbatim block of prior review output so it can never be read back as this pass's
    /// own (task: review cycles after the first, cycle-3 finding, same phantom family as the
    /// placeholder-echo screen in <see cref="ReviewResultParser.ExampleLocationPlaceholder"/>):
    /// <see cref="ReviewResultParser.ParseFindings"/> opens a new finding block on any line whose
    /// TRIMMED text starts with `FINDING:`, with no way to tell "the reviewer just wrote this" from
    /// "the reviewer's summary echoed something quoted earlier in its own prompt" — an observed
    /// habit already tolerated for the VERDICT line. Handing the prior cycle's own findings document
    /// into <see cref="BuildReviewVerify"/> unquoted would put that exact header at the START of a
    /// line the parser reads, so a pass that echoes it back (verifying by quoting, the way a human
    /// reviewer might) manufactures a phantom finding nobody actually reported this cycle. Prefixing
    /// every line — blank ones included, to keep the blockquote intact — with `&gt; ` defeats the
    /// parser's start-of-line check without changing what the text says.
    /// </summary>
    private static string QuoteAsHistory(string text) =>
        string.Join('\n', text.Trim().Split('\n').Select(line => $"> {line.TrimEnd('\r')}"));

    /// <summary>
    /// The `track=` tag a <see cref="BuildReviewVerify"/> pass's finding must carry (task: review
    /// cycles after the first), on top of the shared severity/scope contract
    /// <see cref="AppendFindingContract"/> already states: which of the still-active tracks named
    /// above this finding belongs to. Restating a prior finding's own track (already named in the
    /// prior findings document handed to this pass) is the easy case; a genuinely new finding this
    /// pass discovers on its own needs a considered tag the same way its severity and scope do.
    /// </summary>
    private static void AppendVerifyTrackTagContract(StringBuilder prompt, IReadOnlyList<ReviewLens> tracks)
    {
        const string file = $"{TemplateDirectory}/verify-track-tag.md";
        prompt.AppendLine();
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        prompt.AppendLine(Fragment(
            file, "example",
            ("FindingMarker", ReviewResultParser.FindingMarker),
            ("ExampleLocationPlaceholder", ReviewResultParser.ExampleLocationPlaceholder)));
        prompt.AppendLine();
        AppendFragment(prompt, file, "body");
    }

    /// <summary>
    /// The conformance lens: does the diff do what the task said it would? The objective and
    /// the acceptance criteria are the measuring stick, and repo doctrine (AGENTS.md and the
    /// documents it points at) is the rest of it.
    /// <para>
    /// This track's own convergence stays ungated by severity (Decisions Log #63): a criterion
    /// is met or it is not, so there is no severity ordering for the multi-cycle question —
    /// clean ends the track, and still finding things at its cycle cap parks the run, exactly as
    /// before. It now carries the same structured-finding contract the adversarial pass always
    /// has, though (Decisions Log #87): grading every finding is what lets the platform tell a
    /// genuine defect apart from the docs-phrasing and comment-anchoring nits that used to cost
    /// this lens a full fix-and-re-review cycle each, whatever their actual weight.
    /// </para>
    /// </summary>
    private static string BuildConformanceReview(
        TaskDetails task, ProjectDetails project, string branch, int cycle, ReviewMode mode,
        IReadOnlyList<ReviewParkResolution>? priorRulings,
        IReadOnlyList<ExternalInteractionRecord>? priorHumanDirectedInteractions = null,
        ReviewMechanicsOverride? mechanicsOverride = null, string? sinceSha = null,
        IReadOnlyList<BoundaryApprovalRecord>? priorBoundaryApprovals = null,
        string? interactiveSessionAddress = null,
        bool interactiveModeEnabled = false,
        IReadOnlyList<HumanFixRecord>? priorHumanFixes = null,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/conformance-review.md";
        StringBuilder prompt = new();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            prompt.AppendLine(PromptTemplates.Load(file, "heading-foreign"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "intro-foreign");
            prompt.AppendLine();
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "heading-own"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "intro-own");
            prompt.AppendLine();
        }

        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            prompt.AppendLine(PromptTemplates.Load(file, "what-review-task-is-heading"));
            prompt.AppendLine();
            prompt.AppendLine(task.Objective);
            prompt.AppendLine();
            AppendFragment(prompt, file, "what-review-task-is-body");
            prompt.AppendLine();
            if (task.AcceptanceCriteria.Count > 0)
            {
                prompt.AppendLine(PromptTemplates.Load(file, "review-task-acceptance-criteria-heading"));
                foreach (string criterion in task.AcceptanceCriteria)
                {
                    prompt.AppendLine($"- {criterion}");
                }

                prompt.AppendLine();
            }
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "what-diff-supposed-to-do-heading"));
            prompt.AppendLine();
            prompt.AppendLine(task.Objective);
            prompt.AppendLine();
            prompt.AppendLine(PromptTemplates.Load(file, "acceptance-criteria-heading"));
            foreach (string criterion in task.AcceptanceCriteria)
            {
                prompt.AppendLine($"- {criterion}");
            }

            prompt.AppendLine();
        }

        // Only the pr-review lens needs this: the pull request's own title/description live in
        // agent context (BuildPrReviewLens's own doc), so its conformance basis has nowhere else
        // to come from. Printing it for every task type would change what the ordinary pre-PR
        // conformance lens has always read, which PLAN.md #98's own "does this block the later
        // vision" clause says this branch does not touch (cycle-1 conformance finding).
        if (mechanicsOverride is { DiffIsForeignPullRequest: true } && task.AgentContext.IsNotBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "context-heading"));
            prompt.AppendLine();
            prompt.AppendLine(task.AgentContext);
            prompt.AppendLine();
        }

        AppendSettledRulings(
            prompt, priorRulings, priorHumanDirectedInteractions, mechanicsOverride, priorBoundaryApprovals,
            priorHumanFixes);
        prompt.AppendLine(PromptTemplates.Load(file, "how-to-review-heading"));
        prompt.AppendLine();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "judge-diff-foreign");
        }
        else
        {
            AppendFragment(prompt, file, "judge-work-own");
        }

        if (mechanicsOverride is { DiffIsForeignPullRequest: true }
            && task.ExternalReference.IsNotBlank() && WorkItemContext.CarriesQuotedDescription(task.AgentContext))
        {
            AppendFragment(prompt, file, "adopted-external-item");
        }

        if (project.VerifyCommands.Count > 0 && mechanicsOverride is not { GatesObserved: false })
        {
            AppendFragment(prompt, file, "gates-already-answer-criterion");
        }

        AppendReviewMechanics(
            prompt, project, branch, mode, sinceSha, includesAcceptanceCriteria: true, mechanicsOverride,
            commandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        // Not for a pr-review task's own lens (DiffIsForeignPullRequest): that engine parks on its
        // own findings-report gate (§16 #99), never slice 8's boundaries, so there is no boundary
        // for a milestone message to precede.
        if (interactiveModeEnabled && mechanicsOverride is not { DiffIsForeignPullRequest: true })
        {
            AppendOutboundMilestoneRules(
                prompt, "review", OutboundMilestone.Review, interactiveSessionAddress,
                verdictBoundaryChoicesTaskId: task.Id);
        }

        AppendFindingContract(prompt, project, mode, mechanicsOverride);
        AppendVerdictContract(prompt, cycle, mode, mechanicsOverride);
        prompt.AppendLine();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "hunting-hard-foreign", ("MergeReadyWord", "merge-ready"));
        }
        else
        {
            AppendFragment(prompt, file, "hunting-hard-own", ("MergeReadyWord", "merge-ready"));
        }

        return prompt.ToString();
    }

    /// <summary>
    /// The adversarial lens (Decisions Log #59): a defect hunt that is told nothing about what
    /// the change was supposed to accomplish. Withholding the objective and the acceptance
    /// criteria is the whole mechanism — a reviewer handed the intent reads for alignment with
    /// it, and the defects this pass exists to catch are the ones that are wrong regardless of
    /// intent. Origin incident (2026-08-21, PR #21): a prompt-injection boundary survived every
    /// internal conformance cycle and was caught by an outside reviewer's repeated sampling.
    /// <para>
    /// The defect classes below are named as a warm-up, explicitly not as a checklist: a
    /// checklist becomes the next blind spot, which is the failure this lens exists to fix.
    /// </para>
    /// </summary>
    private static string BuildAdversarialReview(
        Guid taskId, ProjectDetails project, string branch, int cycle, ReviewMode mode,
        IReadOnlyList<ReviewParkResolution>? priorRulings,
        IReadOnlyList<ExternalInteractionRecord>? priorHumanDirectedInteractions = null,
        ReviewMechanicsOverride? mechanicsOverride = null,
        string? sinceSha = null,
        IReadOnlyList<BoundaryApprovalRecord>? priorBoundaryApprovals = null,
        bool interactiveModeEnabled = false,
        string? interactiveSessionAddress = null,
        IReadOnlyList<HumanFixRecord>? priorHumanFixes = null,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/adversarial-review.md";
        StringBuilder prompt = new();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            prompt.AppendLine(PromptTemplates.Load(file, "heading-foreign"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "intro-foreign");
            prompt.AppendLine();
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "heading-own"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "intro-own");
            prompt.AppendLine();
        }

        AppendFragment(prompt, file, "assume-broken");
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "where-defects-hide-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "defect-classes");
        prompt.AppendLine();
        AppendFragment(prompt, file, "defect-classes-tail");
        prompt.AppendLine();
        AppendSettledRulings(
            prompt, priorRulings, priorHumanDirectedInteractions, mechanicsOverride, priorBoundaryApprovals,
            priorHumanFixes);
        prompt.AppendLine(PromptTemplates.Load(file, "how-to-review-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "read-in-surroundings");
        AppendReviewMechanics(
            prompt, project, branch, mode, sinceSha, includesAcceptanceCriteria: false, mechanicsOverride,
            commandTimeout);
        AppendExternalInteractionLoggingRule(prompt, taskId);
        // Not for a pr-review task's own lens (DiffIsForeignPullRequest): see BuildConformanceReview's
        // identical guard for why that engine's park never reaches slice 8's boundaries.
        if (interactiveModeEnabled && mechanicsOverride is not { DiffIsForeignPullRequest: true })
        {
            AppendOutboundMilestoneRules(
                prompt, "review", OutboundMilestone.Review, interactiveSessionAddress,
                verdictBoundaryChoicesTaskId: taskId);
        }

        AppendFindingContract(prompt, project, mode, mechanicsOverride);
        AppendVerdictContract(prompt, cycle, mode, mechanicsOverride);
        prompt.AppendLine();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "hunting-hard-foreign", ("MergeReadyWord", "merge-ready"));
        }
        else
        {
            AppendFragment(prompt, file, "hunting-hard-own", ("MergeReadyWord", "merge-ready"));
        }

        return prompt.ToString();
    }

    /// <summary>How many prior rulings ride into a review prompt — the newest, since they are the ones most likely still relevant.</summary>
    private const int MaxPriorRulings = 8;

    /// <summary>How much of a human's own reason text rides in per ruling — a summary, not the reason restated in full.</summary>
    private const int MaxRulingReasonLength = 500;

    /// <summary>
    /// What a fresh-context review pass is told about questions this task has already settled
    /// (task: review prompts carry prior rulings). Five sources, always in this order:
    /// <list type="number">
    /// <item>This task's own prior <c>h9k review resolve</c> verdicts, if any — bounded to the
    /// newest <see cref="MaxPriorRulings"/> and each reason summarized to
    /// <see cref="MaxRulingReasonLength"/> characters, never the full session transcript that
    /// produced the finding. Origin incidents: the config.json survival ruling was re-litigated
    /// three times across one task's twelve review cycles, and a finding dismissed with
    /// git-ancestry evidence was re-raised verbatim by the next fresh-context reviewer, forcing a
    /// second park over the same question. A <c>--merge-ready</c> ruling and a
    /// <c>--needs-fixes</c> ruling are told apart rather than rendered under one framing: the
    /// former is a dismissal the reviewer should not re-raise without new evidence, but the
    /// latter is the human confirming the defect is real and ordering it fixed — telling a fresh
    /// pass to suppress a re-raise of that same wording would ship an incompletely-fixed defect
    /// the human already confirmed straight past the reviewer that would otherwise catch it.</item>
    /// <item>This task's own logged human directives, if any (<c>h9k task log-interaction
    /// --human-directed</c>, the 2026-09-01 escape-hatch ruling) — filtered to
    /// <c>HumanDirected</c> and bounded the same way, a standing instruction rather than a
    /// ruling on a review park, so the reviewer is told to treat it the same way a needs-fixes
    /// ruling above is treated.</item>
    /// <item>This task's own interactive-mode boundary approvals, if any (<c>h9k review
    /// proceed</c>, #88) — historical context rather than a ruling to weigh: a bare proceed
    /// carries no defect text or redirect, and the section says out loud that it does not mean
    /// interactive mode is on now.</item>
    /// <item>This task's own human-applied fixes, if any (<c>h9k review fixed</c>, task: a human at
    /// the wheel takes the fix role herself) — bounded the same way, and the two shapes told apart
    /// for the same reason the two verdicts above are: a fix with commits settles nothing (they are
    /// in the diff, and checking them is the whole point of the lever), while a
    /// <c>--no-change</c> entry's reason is a dismissal read exactly as a <c>--merge-ready</c>
    /// reason is.</item>
    /// <item>This project's own repo doctrine, named unconditionally rather than quoted and
    /// deliberately generic (the daemon serves whatever project registered it, the same reason
    /// this method's own doctrine sentence hedges "AGENTS.md or CLAUDE.md, and whatever they
    /// point at" rather than naming a file): a project's doctrine can settle a
    /// question at a wider scope than this one task, so the reviewer is told to check whatever
    /// record the project's own AGENTS.md/CLAUDE.md points at (a decisions log, if it keeps one)
    /// rather than assuming this task's project is the platform's own and hardcoding its
    /// PLAN.md §16 into every project's review prompt.</item>
    /// </list>
    /// Appended to BOTH lenses. The adversarial lens is deliberately withheld the task's objective
    /// and acceptance criteria (<see cref="BuildAdversarialReview"/>) so it reads for defects
    /// rather than alignment with intent — but a settled ruling on a review park is a different
    /// kind of fact: it says a question was already asked and answered, not what the change was
    /// trying to do, so handing it to both lenses does not reopen the boundary that method exists
    /// to hold.
    /// <para>
    /// Every sentence that names a file-shaped location (`AGENTS.md`, `CLAUDE.md`) is kept apart
    /// from every sentence that uses defect vocabulary ("not", "departs") — its own paragraph, in
    /// the final trailer below — because <see cref="ReviewVerdictValidation.NamesAFinding"/> reads
    /// the two sharing a sentence (or a location's paragraph immediately followed by one using
    /// defect language) as a reviewer naming a finding. A reviewer that quotes or restates this
    /// prompt text before concluding must not thereby manufacture a "named" finding out of the
    /// platform's own boilerplate — the same class of gap <c>StripPlaceholderLocations</c> and the
    /// objective/criteria strippers already close for this file's other injected text (Decisions
    /// Log #86 origin incident: ten bare needs-fixes verdicts filed 2026-08-25).
    /// </para>
    /// </summary>
    private static void AppendSettledRulings(
        StringBuilder prompt, IReadOnlyList<ReviewParkResolution>? priorRulings,
        IReadOnlyList<ExternalInteractionRecord>? priorHumanDirectedInteractions = null,
        ReviewMechanicsOverride? mechanicsOverride = null,
        IReadOnlyList<BoundaryApprovalRecord>? priorBoundaryApprovals = null,
        IReadOnlyList<HumanFixRecord>? priorHumanFixes = null)
    {
        const string file = $"{TemplateDirectory}/settled-rulings.md";
        if (priorRulings is { Count: > 0 })
        {
            prompt.AppendLine(PromptTemplates.Load(file, "rulings-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "rulings-intro");
            prompt.AppendLine();
            AppendFragment(prompt, file, "rulings-dismissal-meaning", ("MergeReadyWord", "merge-ready"));
            AppendFragment(prompt, file, "rulings-confirmed-defect-meaning", ("NeedsFixesWord", "needs-fixes"));
            prompt.AppendLine();
            foreach (ReviewParkResolution ruling in priorRulings.TakeLast(MaxPriorRulings))
            {
                string verdict = ruling.Verdict == ReviewVerdict.MergeReady ? "merge-ready" : "needs-fixes";
                string resolvedAt = ruling.ResolvedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                prompt.AppendLine(
                    $"- Cycle {ruling.Cycle}, resolved {resolvedAt} as {verdict}: {PrintedReason(ruling)}");
            }

            prompt.AppendLine();
        }

        // Filtered here too, defensively, rather than trusted as already scoped: the whole point
        // of this section is that it never misreports provenance, so a caller that accidentally
        // hands in an agent-initiated entry (HumanDirected: false) must not have it read as a
        // human directive just because it rode in on this list.
        IReadOnlyList<ExternalInteractionRecord> humanDirectedOnly = priorHumanDirectedInteractions is null
            ? []
            : [.. priorHumanDirectedInteractions.Where(interaction => interaction.HumanDirected)];
        if (humanDirectedOnly.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "human-directives-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "human-directives-intro");
            prompt.AppendLine();
            foreach (ExternalInteractionRecord interaction in humanDirectedOnly.TakeLast(MaxPriorRulings))
            {
                string loggedAt = interaction.LoggedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                prompt.AppendLine(
                    $"- {loggedAt}, with {PrintedInteractionParty(interaction)}: " +
                    $"{PrintedInteractionSummary(interaction)} (reason given: {PrintedInteractionReason(interaction)})");
            }

            prompt.AppendLine();
        }

        // A bare h9k review proceed carries no defect text or redirect (task: interactive mode
        // becomes a recorded property of the task) — nothing here for you to re-check or avoid
        // re-raising, unlike the two sections above. This is historical context only: these
        // approvals are permanent, task-wide history, so their presence says a human reviewed a
        // boundary at some point in this task's past — never that interactive mode is on NOW.
        // TaskAggregate.InteractiveModeEnabled is the only current-state source, and it is not
        // threaded into this prompt builder; a task can turn the flag off (h9k task handback, or a
        // default h9k task release) after these approvals were recorded, so asserting present
        // tense here would tell a later,
        // headless-dispatched agent that a human is "actively engaged" when nobody is watching
        // (independent pre-PR review, cycle 1, adversarial lens).
        if (priorBoundaryApprovals is { Count: > 0 })
        {
            prompt.AppendLine(PromptTemplates.Load(file, "boundary-approvals-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "boundary-approvals-intro");
            prompt.AppendLine();
            foreach (BoundaryApprovalRecord approval in priorBoundaryApprovals.TakeLast(MaxPriorRulings))
            {
                string approvedAt = approval.ApprovedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                prompt.AppendLine(Fragment(file, "boundary-approval-item", ("ApprovedAt", approvedAt)));
            }

            prompt.AppendLine();
        }

        // The surface's fourth source (task: a human at the wheel takes the fix role herself): a
        // human took the fix role at the review-verdict-to-fix boundary rather than sending an
        // agent. Two shapes, told apart the way the two verdicts above are, because they ask
        // opposite things. Commits are the ordinary shape and they carry no text at all — the diff
        // under review IS the answer, so nothing here is settled and the fix gets exactly the
        // scrutiny a fix session's would (which is the whole point of the lever: the review agents
        // check a human's fix the same way). A --no-change entry is the dismissal-shaped one: the
        // findings were considered and deliberately left alone, with a stated why, which is the
        // same class of fact a --merge-ready --reason ruling records and is read the same way.
        if (priorHumanFixes is { Count: > 0 })
        {
            prompt.AppendLine(PromptTemplates.Load(file, "human-fixes-heading"));
            prompt.AppendLine();
            AppendFragment(prompt, file, "human-fixes-intro");
            prompt.AppendLine();
            AppendFragment(prompt, file, "human-fixes-with-commits");
            AppendFragment(prompt, file, "human-fixes-no-change", ("MergeReadyWord", "merge-ready"));
            prompt.AppendLine();
            foreach (HumanFixRecord fix in priorHumanFixes.TakeLast(MaxPriorRulings))
            {
                string appliedAt = fix.AppliedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                prompt.AppendLine(fix.NoChangeReason.IsNotBlank()
                    ? $"- Cycle {fix.Cycle}, {appliedAt}, no change: {PrintedNoChangeReason(fix)}"
                    : $"- Cycle {fix.Cycle}, {appliedAt}: fixed by hand, in commits on this branch.");
            }

            prompt.AppendLine();
        }

        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "doctrine-foreign");
            prompt.AppendLine();
            return;
        }

        AppendFragment(prompt, file, "doctrine-own");
        prompt.AppendLine();
    }

    /// <summary>The human's own reason text exactly as this prompt prints it — summarized, never blank.</summary>
    private static string PrintedReason(ReviewParkResolution ruling) =>
        ruling.Reason.IsNotBlank()
            ? RelayedText.Truncate(RelayedText.OneLine(ruling.Reason).Trim(), MaxRulingReasonLength)
            : PromptTemplates.Load($"{TemplateDirectory}/settled-rulings.md", "no-reason-recorded");

    /// <summary>
    /// The prior-ruling reason text this prompt actually prints (the newest
    /// <see cref="MaxPriorRulings"/>, truncated exactly as <see cref="AppendSettledRulings"/>
    /// prints it) — handed to <see cref="ReviewVerdictValidation.NamesAFinding"/> so a reviewer's
    /// verbatim echo of a human's own <c>--reason</c> text is stripped before validation the same
    /// way an echoed task objective or acceptance criterion already is. Restricted to
    /// <see cref="ReviewVerdict.MergeReady"/> rulings: that reason is a dismissal the reviewer is
    /// told not to re-raise, so echoing it back manufactures no new finding. A
    /// <see cref="ReviewVerdict.NeedsFixes"/> reason is the opposite — the human confirming the
    /// defect is real and ordering it fixed — so <see cref="AppendSettledRulings"/> tells the
    /// reviewer to check whether the fix landed and report it again if not; stripping that same
    /// wording out of the reviewer's own re-report would erase the defect language the prompt
    /// just asked for and turn a confirmed, still-unfixed defect into a hollow verdict instead.
    /// </summary>
    internal static IReadOnlyList<string> RulingReasonsShown(IReadOnlyList<ReviewParkResolution>? priorRulings) =>
        priorRulings is null
            ? []
            : [.. priorRulings.TakeLast(MaxPriorRulings)
                .Where(ruling => ruling.Verdict == ReviewVerdict.MergeReady && ruling.Reason.IsNotBlank())
                .Select(PrintedReason)];

    /// <summary>
    /// A <c>--no-change</c> reason exactly as <see cref="AppendSettledRulings"/> prints it —
    /// summarized to <see cref="MaxRulingReasonLength"/> the way every other human free-text field
    /// in that section is. Only ever called for an entry that carries one, which the CLI's own
    /// option shape guarantees is non-blank (<c>--no-change &lt;REASON&gt;</c> takes the reason as
    /// its own argument, the way <c>--needs-fixes</c> does), so there is no "none recorded" arm to
    /// invent: an ordinary human fix has no reason field at all rather than a blank one.
    /// </summary>
    private static string PrintedNoChangeReason(HumanFixRecord fix) =>
        RelayedText.Truncate(RelayedText.OneLine(fix.NoChangeReason ?? string.Empty).Trim(), MaxRulingReasonLength);

    /// <summary>
    /// The <c>--no-change</c> reason text this prompt actually prints (the newest
    /// <see cref="MaxPriorRulings"/>, truncated exactly as <see cref="AppendSettledRulings"/>
    /// prints it) — handed to <see cref="ReviewVerdictValidation.NamesAFinding"/> alongside
    /// <see cref="RulingReasonsShown"/> so a reviewer's verbatim echo of it is stripped before
    /// validation the same way an echoed merge-ready <c>--reason</c> already is. It belongs in that
    /// list for exactly the reason a merge-ready reason does and a needs-fixes one does not: it is
    /// a dismissal the reviewer is told not to re-raise, so echoing it back manufactures no new
    /// finding. An ordinary human fix contributes nothing here — it prints no free text at all, only
    /// the platform's own "fixed by hand, in commits on this branch" sentence.
    /// </summary>
    internal static IReadOnlyList<string> HumanFixNoChangeReasonsShown(
        IReadOnlyList<HumanFixRecord>? priorHumanFixes) =>
        priorHumanFixes is null
            ? []
            : [.. priorHumanFixes.TakeLast(MaxPriorRulings)
                .Where(fix => fix.NoChangeReason.IsNotBlank())
                .Select(PrintedNoChangeReason)];

    /// <summary>The human's own reason text exactly as <see cref="AppendSettledRulings"/> prints it for a logged interaction — summarized, never blank (a human-directed entry always carries one, the CLI command's own requirement).</summary>
    private static string PrintedInteractionReason(ExternalInteractionRecord interaction) =>
        interaction.Reason.IsNotBlank()
            ? RelayedText.Truncate(RelayedText.OneLine(interaction.Reason).Trim(), MaxRulingReasonLength)
            : PromptTemplates.Load($"{TemplateDirectory}/settled-rulings.md", "no-reason-recorded");

    /// <summary>
    /// The <c>--summary</c> text exactly as <see cref="AppendSettledRulings"/> prints it for a
    /// logged interaction — the "what was said or asked, and what you did about it" field the
    /// CLI's own canonical example puts the actual directive in (`--summary "Skip the
    /// workaround"`, `--reason "Real bug"`). Dropping it from the render leaves the reviewer
    /// holding only the human's justification with no statement of what was directed, which is
    /// the defect this method exists to close (independent pre-PR review, cycle 1). Deliberately
    /// not added to the <see cref="ReviewVerdictValidation.NamesAFinding"/> strip lists this file
    /// hands <see cref="Hall9k.Daemon.Review.ReviewEngine"/>: a human-directed entry is treated the
    /// same as a needs-fixes ruling above it, and <see cref="RulingReasonsShown"/>'s own doc
    /// comment already explains why that class of text is left unstripped — the defect language it
    /// may carry is exactly what the reviewer is being told to act on, not dismiss.
    /// </summary>
    private static string PrintedInteractionSummary(ExternalInteractionRecord interaction) =>
        RelayedText.Truncate(RelayedText.OneLine(interaction.Summary).Trim(), MaxRulingReasonLength);

    /// <summary>
    /// The <c>--party</c> text exactly as <see cref="AppendSettledRulings"/> prints it —
    /// bounded to <see cref="MaxRulingReasonLength"/> the same way every other agent-authored
    /// free-text field in this section already is. Unlike <see cref="PrintedReason"/>'s field,
    /// <c>--party</c> carries no length validation at the CLI (<c>TaskLogInteractionCommand.Validate</c>
    /// checks only blankness), so an unbounded print here would let one entry's text dominate every
    /// later review prompt for the task — the same "a ruling is a nudge, not a second history"
    /// reasoning <see cref="AppendSettledRulings"/>'s own doc comment states for the reason field.
    /// </summary>
    private static string PrintedInteractionParty(ExternalInteractionRecord interaction) =>
        RelayedText.Truncate(RelayedText.OneLine(interaction.Party).Trim(), MaxRulingReasonLength);

    /// <summary>
    /// The printed <c>--party</c> text this prompt actually shows (the newest
    /// <see cref="MaxPriorRulings"/>, bounded exactly as <see cref="AppendSettledRulings"/> prints
    /// it) — handed to <see cref="ReviewVerdictValidation.NamesAFinding"/> alongside
    /// <see cref="RulingReasonsShown"/> so a reviewer's verbatim echo of the platform-injected
    /// party text is stripped the same way an echoed objective, criterion, or ruling reason
    /// already is: the defect vocabulary a reviewer's own sentence adds around the echoed span is
    /// what the strip removes, not the location — a <see cref="LocationPattern"/> match inside the
    /// echoed span survives, the same trade-off <see cref="RulingReasonsShown"/>'s own reason text
    /// accepts, and for the identical reason: a real finding can legitimately share a location with
    /// a party string an agent pasted (a plausible reading of <c>--party</c>'s own description
    /// invites pasting a file path into it), and erasing that location along with the echo would
    /// cost that finding its only location the same way over-broadly stripping any of the other
    /// fields here would. This is narrower than "an echo of the party text can never itself supply
    /// the location gate" — a paragraph pairing the echoed location with defect vocabulary from
    /// elsewhere in the same paragraph (most notably a human's own un-stripped ruling reason, which
    /// <see cref="RulingReasonsShown"/> deliberately leaves in needs-fixes-confirming rulings) can
    /// still satisfy <see cref="NamesAFinding"/>'s same-paragraph rule on no real finding of its
    /// own; that residual gap is accepted, not closed, the same way the reason-field trade-off
    /// already is. Unlike <see cref="RulingReasonsShown"/>'s reason text, this is never restricted
    /// to a dismissal-shaped ruling: a party string is identifying information, not a claim about
    /// whether a defect is real, so there is no needs-fixes-confirmation case where stripping it
    /// would erase defect language a human asked the reviewer to check for.
    /// </summary>
    internal static IReadOnlyList<string> HumanDirectedInteractionPartiesShown(
        IReadOnlyList<ExternalInteractionRecord>? priorHumanDirectedInteractions) =>
        priorHumanDirectedInteractions is null
            ? []
            : [.. priorHumanDirectedInteractions
                .Where(interaction => interaction.HumanDirected)
                .TakeLast(MaxPriorRulings)
                .Select(PrintedInteractionParty)];

    /// <summary>
    /// The structured-finding contract every review lens answers in (Decisions Log #63, #87).
    /// Two tags ride on every finding and the platform reads both: a severity, which decides
    /// whether the finding forces another review cycle once the adversarial gate applies AND
    /// whether it earns a fix session of its own this cycle at all, and a scope tag, which
    /// decides whether the fix belongs in this pull request or in a draft bug task of its own.
    /// <para>
    /// The severity anchors are spelled out rather than left to the reviewer's intuition,
    /// because a grade every reviewer invents for itself is not a gate. The scope anchor is
    /// mechanical for the same reason: "the defective line lives in code this branch added or
    /// changed" is checkable against the diff, where "is this really our problem" is not.
    /// </para>
    /// <para>
    /// <paramref name="mode"/> only changes the acceptance-criterion and needs-fixes-bar
    /// paragraphs below, and only on <see cref="ReviewMode.FinalFullPass"/> (Decisions Log
    /// #119): everywhere else, "an unmet acceptance criterion always meets the fix bar" is
    /// still literally true, since <see cref="ReviewFinding.Disposition"/> only narrows that
    /// bar to High alone on a mandatory final pass. Stating that promise unconditionally on a
    /// FinalFullPass dispatch would tell the reviewer something the disposition machinery does
    /// not honor — a Medium-graded acceptance-criterion finding there rides along exactly like
    /// any other in-scope Medium — so this pass gets the true rule instead: grade honestly
    /// against the anchors, and know that only a High blocks a merge-ready verdict here.
    /// </para>
    /// <para>
    /// The Medium anchor no longer absorbs "a doctrine violation that misleads a reader without
    /// corrupting anything" (Decisions Log #134); that clause moved to Low, and the existing
    /// "stale reference" case on Low widened alongside it to cover a misleading stale reference
    /// too, so nothing falls between the two anchors.
    /// Origin: across the event store's full history, 243 of 279 fix cycles dispatched on
    /// Discovery and Verify contained no High finding at all, and the in-scope Medium rate per
    /// Verify pass sat flat at 0.51, 0.47, and 0.50 across cycles 1-2, 3-6, and 7+ — a constant
    /// arrival rate, not a draining defect pool, which is what an honest severity gate should show
    /// as cycles climb. The Medium band was doing two jobs at once: a real defect with bounded or
    /// unlikely impact, which is worth a fix-and-re-review cycle, and a doctrine or prose
    /// violation, which never stops arriving because prose never converges the way a bug count
    /// does. Only the first still meets the fix bar on its own; the second rides along with
    /// whatever cycle is already dispatching, or is recorded as a residual when none is (Decisions
    /// Log #63's ride-along contract, untouched by this change).
    /// </para>
    /// </summary>
    private static void AppendFindingContract(
        StringBuilder prompt, ProjectDetails project, ReviewMode mode,
        ReviewMechanicsOverride? mechanicsOverride = null)
    {
        const string file = $"{TemplateDirectory}/finding-contract.md";
        string baseBranch = mechanicsOverride?.BaseBranch ?? project.BaseBranch;
        // The same boundary AppendReviewMechanics names the read range against, for the same
        // reason: the scope rule below and that range have to agree about what this branch's own
        // work is, or a stacked child's reviewer tags the parent's rewritten-away delta in-scope.
        string scopeBoundary = mechanicsOverride?.ForkPointCommit ?? $"origin/{baseBranch}";
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        AppendFragment(
            prompt, file, "header-shape",
            ("FindingMarker", ReviewResultParser.FindingMarker),
            ("ExampleLocationPlaceholder", ReviewResultParser.ExampleLocationPlaceholder));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "severity-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "severity-anchors");
        prompt.AppendLine();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "severity-bar-foreign");
        }
        else if (mode == ReviewMode.FinalFullPass)
        {
            AppendFragment(prompt, file, "severity-bar-final-full-pass");
        }
        else
        {
            AppendFragment(prompt, file, "severity-bar-ordinary");
        }

        prompt.AppendLine();
        AppendFragment(prompt, file, "grade-exactly");
        prompt.AppendLine();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "fix-bar-foreign", ("NeedsFixesWord", "needs-fixes"), ("MergeReadyWord", "merge-ready"));
        }
        else if (mode == ReviewMode.FinalFullPass)
        {
            AppendFragment(prompt, file, "fix-bar-final-full-pass", ("NeedsFixesWord", "needs-fixes"), ("MergeReadyWord", "merge-ready"));
        }
        else
        {
            AppendFragment(prompt, file, "fix-bar-ordinary", ("NeedsFixesWord", "needs-fixes"), ("MergeReadyWord", "merge-ready"));
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "scope-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "scope-anchors", ("BaseBranch", baseBranch), ("ScopeBoundary", scopeBoundary));
        prompt.AppendLine();
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "scope-report-foreign");
        }
        else
        {
            AppendFragment(prompt, file, "scope-report-ordinary");
        }
    }

    /// <summary>
    /// The mechanics every review pass shares: which diff, verified findings only, read-only,
    /// and the one rule the second lens made necessary — no builds and no test runs.
    /// A cycle's passes read the same worktree; at the default session cap they run
    /// concurrently (log #59), and even at a lower cap (Decisions Log #111) that instead
    /// serializes them within the cycle, no individual pass can tell from inside the sandbox
    /// whether another is sharing the worktree at that instant. Two builds that do overlap
    /// would share one `obj/`/`bin/` and fail each other with file-in-use errors, so the rule
    /// holds unconditionally rather than leaving a pass to guess. A pass that reports a
    /// collision like that as a verified finding spends the cycle's one fix run on a platform
    /// failure, so the prompt also says plainly that the gates already answered the build
    /// question and are not to be re-run.
    /// <para>
    /// The diff instruction itself narrows for a <see cref="ReviewMode.FinalFullPass"/> pass with a
    /// resolved <paramref name="sinceSha"/> (task: the mandatory FinalFullPass rereads only the
    /// commits no full-scope pass has already read, Decisions Log #115), and for a
    /// <see cref="ReviewMode.Discovery"/> pass with a resolved <paramref name="sinceSha"/> — a
    /// ReviewFeedback or FailingChecks follow-up's own opening cycle (task: a lap reviews only what
    /// it changed): every other combination — an ordinary Discovery dispatch, or a FinalFullPass
    /// with no prior full-scope read on record — reads the same full base-branch three-dot diff
    /// this method has always instructed. <see cref="ReviewMode.Verify"/> never reaches this method
    /// with its own scoped instruction at all; that mode has its own prompt builder entirely
    /// (<see cref="BuildReviewVerify"/>).
    /// </para>
    /// <para>
    /// The scoped block's acceptance-criteria sentence is gated on
    /// <paramref name="includesAcceptanceCriteria"/>: <see cref="BuildConformanceReview"/> prints
    /// the criteria (above this section) and passes <see langword="true"/>, but
    /// <see cref="BuildAdversarialReview"/> never prints an objective or acceptance criteria at
    /// all (Decisions Log #59 — that withholding is the whole mechanism that keeps the lens
    /// reading for defects rather than intent-alignment) and passes <see langword="false"/>, so
    /// this method never points that lens at a section its own prompt does not contain.
    /// </para>
    /// <para>
    /// Also states the foreground-gates rule (<see cref="AppendForegroundGatesRule"/>,
    /// <c>sessionRunsGates: false</c>), the same one <see cref="BuildReviewVerify"/> already
    /// carried: this read-only pass's process is torn down by <c>TerminateTree</c> exactly like
    /// every other headless leg's once its result arrives (<c>ReviewEngine.WaitForSessionResultAsync</c>,
    /// <c>PrReviewEngine</c>'s own wait site), so a discovery or adversarial pass — or the
    /// pr-review lens, which delegates here through <see cref="BuildPrReviewLens"/> — that
    /// backgrounds a command and ends its turn hits the identical dead end a build or fix session
    /// does, with nothing in its prompt having said so beforehand (independent pre-PR review,
    /// cycle 3, adversarial lens — the rule previously reached only <see cref="BuildReviewVerify"/>,
    /// leaving the structurally identical read-only bullet here silent).
    /// </para>
    /// </summary>
    private static void AppendReviewMechanics(
        StringBuilder prompt, ProjectDetails project, string branch, ReviewMode mode, string? sinceSha,
        bool includesAcceptanceCriteria, ReviewMechanicsOverride? mechanicsOverride = null,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/review-mechanics.md";
        string baseBranch = mechanicsOverride?.BaseBranch ?? project.BaseBranch;
        // What every range below is taken against: a stacked child's own recorded fork point as a
        // literal commit, and `origin/<base>` for every other pass — see
        // ReviewMechanicsOverride.ForkPointCommit for the force-pushed-parent hazard that makes the
        // ref wrong there, and AppendStackedScopeBoundaryReason for the sentence a commit carries.
        string? forkPoint = mechanicsOverride?.ForkPointCommit;
        string scopeBoundary = forkPoint ?? $"origin/{baseBranch}";
        prompt.AppendLine(mechanicsOverride?.CheckoutDescription
            ?? Fragment(file, "checkout-description", ("Branch", branch)));
        if (mode == ReviewMode.FinalFullPass && sinceSha is { } fullScopeSha)
        {
            AppendFragment(prompt, file, "final-full-pass-lead", ("FullScopeSha", fullScopeSha));
            if (includesAcceptanceCriteria)
            {
                AppendFragment(prompt, file, "final-full-pass-acceptance-criteria", ("FullScopeSha", fullScopeSha));
            }

            AppendFragment(
                prompt, file, "final-full-pass-range",
                ("FullScopeSha", fullScopeSha), ("BaseBranch", baseBranch), ("ScopeBoundary", scopeBoundary),
                ("MergeReadyWord", "merge-ready"));
            if (forkPoint is not null)
            {
                AppendStackedScopeBoundaryReason(prompt, baseBranch, PromptTemplates.Load($"{TemplateDirectory}/stacked-scope-boundary.md", "lead-in-mid-sentence"));
            }
            else
            {
                AppendFragment(prompt, file, "final-full-pass-no-fork-point", ("BaseBranch", baseBranch));
            }
        }
        else if (mode == ReviewMode.Discovery && sinceSha is { } lapSinceSha)
        {
            AppendFragment(prompt, file, "discovery-lap-lead", ("LapSinceSha", lapSinceSha));
            if (includesAcceptanceCriteria)
            {
                AppendFragment(prompt, file, "discovery-lap-acceptance-criteria", ("LapSinceSha", lapSinceSha));
            }

            AppendFragment(
                prompt, file, "discovery-lap-scope", ("ScopeBoundary", scopeBoundary), ("BaseBranch", baseBranch));
            if (forkPoint is not null)
            {
                AppendFragment(prompt, file, "discovery-lap-fork-point-lead");
                AppendStackedScopeBoundaryReason(prompt, baseBranch, PromptTemplates.Load($"{TemplateDirectory}/stacked-scope-boundary.md", "lead-in-mid-sentence"));
            }
            else
            {
                AppendFragment(prompt, file, "discovery-lap-no-fork-point", ("BaseBranch", baseBranch));
            }
        }
        else if (forkPoint is not null)
        {
            AppendFragment(prompt, file, "stacked-diff-range", ("ScopeBoundary", scopeBoundary));
            AppendStackedScopeBoundaryReason(prompt, baseBranch, PromptTemplates.Load($"{TemplateDirectory}/stacked-scope-boundary.md", "lead-in-sentence-head"));
        }
        else
        {
            AppendFragment(prompt, file, "ordinary-diff-range", ("BaseBranch", baseBranch));
        }

        AppendFragment(prompt, file, "report-verified-findings");
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "no-build-foreign");
        }
        else
        {
            AppendFragment(prompt, file, "no-build-ordinary");
        }

        AppendForegroundGatesRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: false);
        AppendReviewGateStatus(prompt, project, mechanicsOverride?.GatesObserved ?? true);
    }

    /// <summary>
    /// Why a stacked child's ranges name a commit where every other pass names a ref — the same
    /// explanation the build session's own self-review range carries
    /// (<c>WorkPromptBuilder.AppendSelfReviewPhaseRules</c>), so a reviewer handed an unfamiliar
    /// boundary is told what it is rather than left to substitute the ref back in.
    /// <paramref name="leadIn"/> is the fragment the sentence starts with, because the three
    /// mechanics arms reach it mid-sentence and at the head of one.
    /// </summary>
    private static void AppendStackedScopeBoundaryReason(StringBuilder prompt, string baseBranch, string leadIn) =>
        AppendFragment(
            prompt, $"{TemplateDirectory}/stacked-scope-boundary.md", "reason",
            ("LeadIn", leadIn), ("BaseBranch", baseBranch));

    /// <summary>
    /// What the platform already observed about this commit, so a reviewer told not to build
    /// knows the question was answered rather than skipped. VerificationRunner runs the
    /// project's gates immediately before the review loop is entered, and again on every
    /// re-verify, so this is a stated observation and not a promise.
    /// </summary>
    private static void AppendReviewGateStatus(StringBuilder prompt, ProjectDetails project, bool gatesObserved = true)
    {
        const string file = $"{TemplateDirectory}/review-gate-status.md";
        if (!gatesObserved)
        {
            AppendFragment(prompt, file, "not-observed");
            return;
        }

        IReadOnlyList<VerifyCommand> gates = project.VerifyCommands;
        if (gates.Count == 0)
        {
            AppendFragment(prompt, file, "no-gates-configured");
            return;
        }

        AppendFragment(prompt, file, "gates-passed");
        foreach (VerifyCommand gate in gates)
        {
            prompt.AppendLine($"  - `{gate.Command}`");
        }
    }

    /// <summary>
    /// The VERDICT-line contract, identical for every lens: the daemon parses this line, and a
    /// pass that ends without one gets the cycle's single re-prompt (log #59 — the re-prompt
    /// belongs to the cycle, not to each lens) before the run parks for a human. The pr-review
    /// lens (<paramref name="mechanicsOverride"/>) is the one exception: <c>PrReviewEngine</c>
    /// still parses this line (<c>PrReviewEngine.HasUsableVerdict</c>), but has no cycle to
    /// re-prompt within, so a missing verdict fails the run outright rather than costing a
    /// same-session retry (cycle-1 adversarial finding, this method's own former claim was
    /// false for that lens).
    /// <para>
    /// <paramref name="mode"/>'s needs-fixes trigger has to agree with
    /// <see cref="AppendFindingContract"/>'s own bar (independent pre-PR review, cycle 2,
    /// adversarial finding): that method already tells a <see cref="ReviewMode.FinalFullPass"/>
    /// reviewer that only a `high` finding blocks a merge-ready verdict, but this method used to
    /// say "medium or high" unconditionally a few paragraphs later — the section a reviewer
    /// reads last, immediately before writing its verdict — so a pass holding one in-scope
    /// Medium was told two opposite things by the same prompt. Every other cycle keeps the
    /// ordinary medium-or-high bar; only <see cref="ReviewMode.FinalFullPass"/> narrows it here.
    /// </para>
    /// </summary>
    private static void AppendVerdictContract(
        StringBuilder prompt, int cycle, ReviewMode mode, ReviewMechanicsOverride? mechanicsOverride = null)
    {
        const string file = $"{TemplateDirectory}/verdict-contract.md";
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        prompt.AppendLine(Fragment(
            file, "verdict-line-ready", ("VerdictMarker", ReviewResultParser.VerdictMarker), ("MergeReadyWord", "merge-ready")));
        prompt.AppendLine();
        if (mode == ReviewMode.FinalFullPass)
        {
            AppendFragment(prompt, file, "verdict-ready-condition-final-full-pass", ("NeedsFixesWord", "needs-fixes"));
        }
        else
        {
            AppendFragment(prompt, file, "verdict-ready-condition-ordinary", ("NeedsFixesWord", "needs-fixes"));
        }

        prompt.AppendLine();
        prompt.AppendLine(Fragment(
            file, "verdict-fix-needed-line", ("VerdictMarker", ReviewResultParser.VerdictMarker), ("NeedsFixesWord", "needs-fixes")));
        prompt.AppendLine();
        if (mode == ReviewMode.FinalFullPass)
        {
            AppendFragment(prompt, file, "verdict-fix-condition-final-full-pass", ("NeedsFixesWord", "needs-fixes"));
        }
        else
        {
            AppendFragment(prompt, file, "verdict-fix-condition-ordinary", ("NeedsFixesWord", "needs-fixes"));
        }

        AppendFragment(prompt, file, "wait-and-parse-lead", ("DeliverWord", "deliver"));
        if (mechanicsOverride is { DiffIsForeignPullRequest: true })
        {
            AppendFragment(prompt, file, "no-verdict-foreign", ("NeedsFixesWord", "needs-fixes"));
        }
        else
        {
            AppendFragment(prompt, file, "missing-verdict-ordinary", ("Cycle", cycle.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// The one same-session retry for a reviewer whose verdict the engine could not honestly
    /// act on: no VERDICT line at all, or a needs-fixes verdict naming nothing
    /// (<see cref="Hall9k.Daemon.Review.ReviewVerdictValidation"/>). The session resumes (it
    /// already read the diff) and is told to conclude now. One re-prompt only — a second
    /// verdict-less ending parks the run (log #11 spirit). Origin incidents: 2026-08-18, the
    /// first live review ended with a promise to deliver the verdict "when it completes" and
    /// parked a correct implementation; 2026-08-25, ten occurrences of a needs-fixes verdict
    /// that named no finding either parked a human or burned a fix session on content that did
    /// not exist.
    /// <para>
    /// The resumed leg's output <i>replaces</i> what the platform read from the first one
    /// (<c>ReviewEngine.RecordReviewPassAsync</c> re-parses it and overwrites the lens's
    /// findings file), so the structured contract every lens now answers in (Decisions Log #87)
    /// is told again here. Asking a pass to restate its findings as plain prose would strip the
    /// severity and scope tags off every one of them, and the loop would then read a graded,
    /// placed set of findings as one ungraded, unplaced stand-in.
    /// </para>
    /// <para>
    /// The merge-ready path stated here is deliberately not offered as a plain alternative to
    /// restating (independent pre-PR review, cycle 2, adversarial finding): the heuristic this
    /// reprompt exists downstream of is a keyword-and-proximity check with a disclosed,
    /// permanent vocabulary gap, so a demotion to Unknown is not proof the original finding was
    /// hollow — it may just be phrased outside the words the platform recognizes. Framing
    /// merge-ready as available "if none stand" invited a session to read its own rejection as
    /// license to drop a finding it still believed, so the wording now says plainly that a
    /// demotion is not a verdict on the finding's truth and gates merge-ready on genuine
    /// reconsideration rather than restatement fatigue.
    /// </para>
    /// <para>
    /// <paramref name="verifyTracks"/> is non-null only when the pass being re-prompted ran under
    /// <see cref="ReviewMode.Verify"/> (independent pre-PR review, cycle 2, adversarial finding):
    /// that pass's own contract carries a <c>track=</c> tag on top of severity and scope
    /// (<see cref="AppendVerifyTrackTagContract"/>), and since the resumed leg's output replaces
    /// the original's in full, omitting it here would have a restated finding arrive untagged and
    /// get attributed to every active track rather than the one it actually belongs to.
    /// </para>
    /// <para>
    /// <paramref name="mechanicsOverride"/> is the one the pass being re-prompted was dispatched
    /// with — its run's own recorded base pair, threaded through rather than recomputed here
    /// (routed finding, run 01a07933, conformance lens, cycle 5): the finding contract below
    /// carries the scope rule, and a resumed session handed
    /// <c>origin/{project.BaseBranch}</c> when its original prompt scoped it against a stacked
    /// parent's branch or that run's recorded fork point would grade parent-owned lines in-scope on
    /// the child's pull request. Null for every ordinary run, which is what keeps an unstacked
    /// re-prompt byte-identical.
    /// </para>
    /// </summary>
    public static string BuildReviewVerdictReprompt(
        ProjectDetails project, int cycle, ReviewMode? mode = null,
        IReadOnlyList<ReviewLens>? verifyTracks = null,
        ReviewMechanicsOverride? mechanicsOverride = null)
    {
        const string file = $"{TemplateDirectory}/review-verdict-reprompt.md";
        ReviewMode resolvedMode = mode ?? ReviewMode.Discovery;
        StringBuilder prompt = new();
        AppendFragment(prompt, file, "intro", ("NeedsFixesWord", "needs-fixes"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "wait-for-checks");
        AppendFragment(prompt, file, "restate-finding", ("MergeReadyWord", "merge-ready"));
        if (resolvedMode == ReviewMode.FinalFullPass)
        {
            AppendFragment(
                prompt, file, "fix-bar-final-full-pass",
                ("NeedsFixesWord", "needs-fixes"), ("MergeReadyWord", "merge-ready"));
        }
        else
        {
            AppendFragment(
                prompt, file, "fix-bar-ordinary",
                ("NeedsFixesWord", "needs-fixes"), ("MergeReadyWord", "merge-ready"));
        }

        AppendFragment(
            prompt, file, "end-with-verdict-line",
            ("VerdictMarker", ReviewResultParser.VerdictMarker), ("MergeReadyWord", "merge-ready"),
            ("NeedsFixesWord", "needs-fixes"));
        AppendFindingContract(prompt, project, resolvedMode, mechanicsOverride);
        if (verifyTracks is { Count: > 0 })
        {
            AppendVerifyTrackTagContract(prompt, verifyTracks);
        }

        prompt.AppendLine();
        AppendFragment(prompt, file, "closing", ("Cycle", cycle.ToString(CultureInfo.InvariantCulture)));

        return prompt.ToString();
    }

    /// <summary>
    /// The retry leg of token-budget recovery (backlog 40): the same session resumes
    /// after the subscription usage window very likely reset, with the full transcript and
    /// worktree exactly as the exhausted attempt left them. No task or project context is
    /// restated — a resumed session already has all of it — this is only the nudge to
    /// continue rather than restart. The one exception is
    /// <see cref="WorkPromptBuilder.AppendNoHostLoadForFlakeReproductionRule"/>: a resumed
    /// session otherwise never sees that rule at all, since this leg calls neither
    /// <see cref="WorkPromptBuilder.AppendForegroundGatesRule"/> nor
    /// <see cref="WorkPromptBuilder.AppendSessionEndsAtFinalMessageRule"/>, and a session mid-fix
    /// on a flaky test is exactly the one this rule most needs to reach.
    /// <paramref name="task"/> decides which half of that rule applies
    /// (independent pre-PR review, cycle 1, both lenses): a pr-review task's primary session is
    /// the read-only adversarial lens over another contributor's pull request
    /// (<c>PrimarySessionResumer.ResumeAsync</c>'s own <c>UntrustedWorkingDirectory</c> check),
    /// and this leg is what resumes it — <c>TokenBudgetRetryEngine.RetryOneAsync</c> diverts only
    /// the conformance-lens exhaustion case back into the review loop; a primary-session
    /// exhaustion falls through to here regardless of task type. Telling that resumed session to
    /// inject a fix and "run the suite once, in the foreground" would have it edit and execute a
    /// foreign pull request's own code under the owner's credentials, exactly the shape
    /// <see cref="BuildReviewVerify"/> and <see cref="BuildUncommittedWorkRecovery"/> already
    /// avoid via <c>sessionRunsGates: false</c>.
    /// </summary>
    public static string BuildBudgetRetry(TaskDetails task)
    {
        StringBuilder prompt = new();
        AppendFragment(prompt, $"{TemplateDirectory}/budget-retry.md", "body");
        prompt.AppendLine();
        WorkPromptBuilder.AppendNoHostLoadForFlakeReproductionRule(
            prompt, sessionRunsGates: task.Type != TaskType.PrReview);

        return prompt.ToString();
    }

    /// <summary>
    /// The retry leg of session-error-result recovery (task: a session that reports an error
    /// result is retried once in place): the same session resumes after a terminal result that
    /// carried a generic error rather than the recognizable usage-limit shape
    /// <see cref="BuildBudgetRetry"/> answers — most likely a transient provider-side hiccup
    /// (measured 2026-09-05: 41 such failures land in bursts across only 18 distinct hours, the
    /// signature of an overload or rate-limit window rather than a defect in the work itself) —
    /// with the full transcript and worktree exactly as the errored attempt left them. No task
    /// or project context is restated — a resumed session already has all of it — this is only
    /// the nudge to continue rather than restart, with the same one exception
    /// <see cref="BuildBudgetRetry"/> carries:
    /// <see cref="WorkPromptBuilder.AppendNoHostLoadForFlakeReproductionRule"/>, gated on
    /// <paramref name="task"/> the same way <see cref="BuildBudgetRetry"/> is — this leg resumes
    /// the same read-only pr-review primary session (<c>RunSupervisor.ResumeBuildSessionErrorRetryAsync</c>
    /// resumes whichever session <see cref="RunSessionLeg.Build"/> names, pr-review's adversarial
    /// lens included) and must not tell it to edit and run a foreign pull request's own code.
    /// </summary>
    public static string BuildSessionErrorRetry(TaskDetails task)
    {
        StringBuilder prompt = new();
        AppendFragment(prompt, $"{TemplateDirectory}/session-error-retry.md", "body");
        prompt.AppendLine();
        WorkPromptBuilder.AppendNoHostLoadForFlakeReproductionRule(
            prompt, sessionRunsGates: task.Type != TaskType.PrReview);

        return prompt.ToString();
    }

    /// <summary>
    /// The one automatic uncommitted-work recovery session (task: when a session ends with
    /// finished work uncommitted, the daemon recovers on its own): a FRESH session — never a
    /// <c>--resume</c> of the errored one, unlike <see cref="BuildSessionErrorRetry"/> and
    /// <see cref="BuildBudgetRetry"/> above — spawned into the same retained worktree the prior
    /// session left dirty. Fresh rather than resumed on purpose: a resumed session would still
    /// carry whatever review findings or fix instructions the prior session was working from,
    /// and this session's entire job is committing what is already finished, not reasoning about
    /// any of that again. No task or project context beyond the objective is restated —
    /// deliberately narrow, so it stays cheap by construction. Unlike a `--resume`, this session
    /// starts with zero memory of the branch, which is exactly why it is never asked to judge
    /// whether a listed file belongs: with no memory of why the prior session left it that way, a
    /// fresh session guessing "abandoned debugging" and discarding real work is indistinguishable
    /// from a fresh session guessing right, so every listed file is committed, unconditionally,
    /// and no file is ever reverted or deleted.
    /// <para>
    /// None of the files named below may be reverted or deleted, even when one of them looks like
    /// scratch state: the platform's own re-check (<c>VerificationRunner.RecordRecoveryOutcomeAsync</c>)
    /// verifies afterward that each one actually reached a commit, byte for byte — not merely that
    /// it stopped showing up in `git status` — so discarding one does not pass this check either,
    /// it only loses the work while still failing the run. Earlier wording here authorized exactly
    /// that discard, and separately claimed the tree had to show nothing at all in `git status`,
    /// which is a wider bar than what the platform's own detector (`WorktreeGitStatus.SplitUntracked`)
    /// actually enforces — a build/test byproduct outside <c>src/</c> or <c>tests/</c> is warn-only
    /// there and was never anyone's to delete (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    public static string BuildUncommittedWorkRecovery(
        TaskDetails task, IReadOnlyList<string> strandedFiles, bool priorSessionReportedBackgroundWait = false,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/uncommitted-work-recovery.md";
        StringBuilder prompt = new();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        prompt.AppendLine(SummarizeStrandedFiles(strandedFiles));
        prompt.AppendLine();
        if (priorSessionReportedBackgroundWait)
        {
            AppendFragment(prompt, file, "background-wait-notice");
            prompt.AppendLine();
        }
        prompt.AppendLine(Fragment(file, "objective-orientation", ("Objective", task.Objective)));
        prompt.AppendLine();
        AppendFragment(prompt, file, "only-job");
        prompt.AppendLine();
        AppendFragment(prompt, file, "no-revert");
        prompt.AppendLine();
        AppendFragment(prompt, file, "leave-others-alone");
        prompt.AppendLine();
        AppendFragment(prompt, file, "no-findings-no-gates");
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "session-ends-note");
        AppendForegroundGatesRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: false);
        AppendExternalInteractionLoggingRule(prompt, task.Id);

        return prompt.ToString();
    }

    /// <summary>
    /// Every stranded file named for the recovery prompt above — a wider cap than
    /// <c>VerificationRunner.SummarizeFiles</c>'s own <c>MaxListedFiles</c> (20) uses for the
    /// one-line failure reason it is built from, deliberately: a session recovering from the
    /// strand needs to see every file it might need to act on, not the eliding summary a human
    /// reads in a failure message, so this prompt affords more room before it elides too.
    /// </summary>
    private const int MaxListedRecoveryFiles = 30;

    private static string SummarizeStrandedFiles(IReadOnlyList<string> strandedFiles) =>
        strandedFiles.Count <= MaxListedRecoveryFiles
            ? string.Join('\n', strandedFiles.Select(file => $"- {file}"))
            : string.Join('\n', strandedFiles.Take(MaxListedRecoveryFiles).Select(file => $"- {file}"))
              + $"\n- and {strandedFiles.Count - MaxListedRecoveryFiles} more (run `git status` for the rest)";

    /// <summary>
    /// The fix leg of the review loop (Decisions Log #23): a fresh session resolves the
    /// reviewers' verified findings in the same worktree. One fix session per cycle handles
    /// every track's findings together (log #59) — the findings it is handed are the cycle's
    /// merged document, with each finding under the lens that produced it and the platform's
    /// disposition for it recorded underneath (log #63). Disputes park for a human instead of
    /// looping — the daemon parses the resolution line.
    /// <para>
    /// The dispute lever covers a finding's severity as well as the finding itself, which is
    /// what keeps the severity gate a gate: an agent that could quietly re-grade a High as a
    /// Low would be deciding its own way past the convergence rule.
    /// </para>
    /// </summary>
    public static string BuildReviewFix(
        TaskDetails task, ProjectDetails project, string branch, string findings, int cycle,
        string? interactiveSessionAddress = null,
        bool? interactiveModeEnabledOverride = null,
        string? baseBranch = null,
        string? baseCommit = null,
        TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/review-fix.md";
        bool interactiveModeEnabled = interactiveModeEnabledOverride ?? task.InteractiveModeEnabled;
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "original-objective-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "review-findings-heading", ("Cycle", cycle.ToString(CultureInfo.InvariantCulture))));
        prompt.AppendLine();
        prompt.AppendLine(findings);
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("Branch", branch));
        AppendFragment(prompt, file, "verify-and-fix");
        AppendSessionEndsAtFinalMessageRule(prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        AppendFragment(prompt, file, "disposition-lead", ("DispositionsHeading", ReviewFindingDispositions.Heading));
        prompt.AppendLine(Fragment(file, "disposition-fix-here", ("FixHere", ReviewFindingDispositions.FixHere)));
        AppendFragment(
            prompt, file, "disposition-fix-here-own-commit",
            ("FixHereInItsOwnCommit", ReviewFindingDispositions.FixHereInItsOwnCommit));
        AppendFragment(prompt, file, "disposition-do-not-fix-here", ("DoNotFixHere", ReviewFindingDispositions.DoNotFixHere));
        AppendFragment(prompt, file, "disposition-ride-along", ("RideAlong", ReviewFindingDispositions.RideAlong));
        AppendFragment(prompt, file, "judgment-dispute");
        // Opportunistic, not mandatory (Decisions Log #163): the build session
        // already composed this pull request's summary, and a fix folded into its owning commit
        // usually changes nothing a reviewer of the whole change needs to know. A session that
        // writes no block leaves the build session's own standing, which is why the sentence asks
        // rather than requires. The dedicated pre-open summary session is the named upgrade if
        // reviewers start reporting bodies that no longer describe the diff.
        AppendFragment(
            prompt, file, "pr-summary-refresh",
            ("PrSummaryMarker", PrSummaryParser.Marker), ("PrSummaryTitlePrefix", PrSummaryParser.TitlePrefix));
        AppendWritingConventions(
            prompt, "  ", project.WritingConventions, PromptTemplates.Load(file, "writing-conventions-lead-in"));
        AppendReviewFixSelfCheckPhaseRules(
            prompt, project, effectiveBaseBranch,
            commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout,
            WorkPromptBuilder.StackedForkPoint(project, effectiveBaseBranch, baseCommit));

        // Last, not immediately after AppendExternalInteractionLoggingRule (independent pre-PR
        // review, cycle 1, both lenses): this method opens its own "##" heading, so calling it
        // mid-list nested every rule appended after it — the disposition contract, the dispute
        // rule, the self-check phase — under "Reporting to the human" instead of under
        // "## Working rules".
        if (interactiveModeEnabled)
        {
            AppendOutboundMilestoneRules(prompt, "fix", OutboundMilestone.Fix, interactiveSessionAddress);
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "resolution-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "resolution-intro");
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "resolution-fixed-line", ("ResolutionMarker", "RESOLUTION:")));
        prompt.AppendLine();
        AppendFragment(prompt, file, "resolution-fixed-condition");
        prompt.AppendLine();
        prompt.AppendLine(Fragment(file, "resolution-disputed-line", ("ResolutionMarker", "RESOLUTION:")));
        prompt.AppendLine();
        AppendFragment(prompt, file, "resolution-disputed-condition");

        return prompt.ToString();
    }

    /// <summary>
    /// The review-fix session's own self-check phase (task: the review fix session ends with a
    /// mandatory self-check phase before handing back), scaled down from the build session's
    /// adversarial self-review loop (<see cref="WorkPromptBuilder.AppendSelfReviewPhaseRules"/>)
    /// to the size a fix round actually is: small and targeted, so this is one pass, not a loop.
    /// Ordered after every finding is fixed or disputed and before the resolution line, so the
    /// hunt sees the finished fix rather than a mid-flight one.
    /// <para>
    /// Origin, both from one afternoon (2026-08-30): cea5ae6e cycle 6 landed a reflog fix on one
    /// of two branch-creating arms that needed it — the verify pass's blast-radius check caught
    /// the sibling only because it happened to be the Opus review model, not the fix model —
    /// and b6dfcbe5's park found a two-escape cancellation finding with only one escape closed.
    /// A third instance, cea5ae6e cycle 8, was the regression class: the second, hurried
    /// application of the same reflog fix swapped create-only branch materialisation for a
    /// silent force-move. All three would have been caught by their own fix session's author,
    /// which is the whole point of running this before the verify pass has to. Not sequenced
    /// ahead of the review-model-to-fix-model knob (Decisions Log #92, #105): that knob already
    /// landed on main (9f75a6e1, committed 2026-08-31) before this phase's own first commit
    /// (2026-09-01), so this phase arrives as the compensating control after it rather than
    /// ahead of it. The knob itself ships blank and falls through to the plain review model
    /// (<see cref="RoleModelDefaults.ReviewVerify"/> is empty and nothing seeds it), so the
    /// cheaper-verify-pass exposure is live only on an install that has set it — the standard
    /// install being one, since AGENTS.md records it pointing the knob at the fix model. Both
    /// catches above predate the knob entirely (2026-08-30) and were made on the review model;
    /// what they establish is the failure mode this phase catches, not that a cheaper verify
    /// pass already missed it.
    /// </para>
    /// <para>
    /// The clean-tree closing line and the foreground-test instruction are their own origins,
    /// separate from the self-check phase itself: strandings #8 (a94dcd35) and #9 (70d5e8de),
    /// both 2026-08-31 and both review-fix sessions, each completed a coherent cycle fix and
    /// ended without committing it — caught only by <c>VerificationRunner</c>'s pre-gate check,
    /// at the cost of an operator salvage-and-retry lap. The fix prompt already carried this
    /// contract by then, via <see cref="AppendSessionEndsAtFinalMessageRule"/> (backlog 57,
    /// landed 2026-08-27 in d30c3162, four days before both strandings): the gap the strandings
    /// expose is an instruction ignored, not one missing, so the line below repeats it at a
    /// more specific point — immediately after this phase's own hunt, which can itself leave
    /// new work uncommitted — rather than introducing a contract the prompt never had. And
    /// 2026-09-01 transcript mining across 399 fix sessions found the command tool's 2-minute
    /// default killing obedient foreground test runs of an 8-minute suite — sessions adapted by
    /// detaching the run and then dying waiting on it — while every clean full-suite survivor
    /// had passed an explicit 590-600 second timeout.
    /// </para>
    /// <para>
    /// Deliberately narrow, matching the build session's own phase: no model change (the fix
    /// session keeps whatever model it already resolved to, so a before/after verify-pass
    /// comparison attributes to the prompt alone), and scoped to this one prompt — the build
    /// prompt and the review lens prompts are untouched.
    /// </para>
    /// <para>
    /// The sweep's fixing bound carries one carve-out (this task's own cycle-3 review): a
    /// pre-existing sibling stays named-not-fixed only when the finding being swept is not
    /// itself dispositioned <see cref="ReviewFindingDispositions.FixHereInItsOwnCommit"/>. When
    /// it is, that disposition already decided this defect's shape belongs in its own commit
    /// here, so leaving a sibling merely named — rather than fixed in that same commit —
    /// contradicts the disposition rule and reintroduces the exact extra lap this phase exists to
    /// remove: cea5ae6e's CreateAsync sibling was left for later instead of fixed alongside the
    /// finding that shared its shape, and still had to be fixed in-PR anyway, as 59dc9bba.
    /// </para>
    /// <para>
    /// Cycle 8 review corrected two more claims. The sweep's own-changes boundary is now drawn
    /// from <c>origin/{project.BaseBranch}</c>, not the fix session's local base-branch ref —
    /// this prompt previously named the boundary without saying what to measure it against, and
    /// AGENTS.md records that ref as routinely stale, the same reason
    /// <see cref="AppendReviewMechanics"/> and the rebase mechanics both qualify it with
    /// <c>origin/</c>. And the sweep's prompt text no longer claims a pre-existing sibling named
    /// in the fix summary reaches "out-of-scope routing" directly: <c>RouteFindingsAsync</c>
    /// mints and folds findings only out of a review pass's own parsed output, never a fix
    /// session's summary, so a named sibling reaches routing only if a later Verify pass reads
    /// the summary back and reports it as a finding of its own — exactly the same hedge this
    /// phase's single-pass-not-a-loop paragraph above already states, which the sweep's own
    /// wording had drifted out of step with.
    /// </para>
    /// <para>
    /// Cycle 10 review found the <see cref="ReviewFindingDispositions.FixHereInItsOwnCommit"/>
    /// carve-out fixing-every-sibling with no exception for a sibling that this same findings
    /// document separately dispositions <see cref="ReviewFindingDispositions.DoNotFixHere"/>: one
    /// adversarial pass reporting the same defect shape at two pre-existing sites can land one
    /// under "fix in its own commit" (out-of-scope, High) and the other under "routed away"
    /// (out-of-scope, Medium/Low, per <c>ReviewFinding.cs:69</c>) in the same cycle document, and
    /// the carve-out as written ordered the sweep to fix the routed-away one anyway, contradicting
    /// its own disposition and fixing a defect a draft bug task or the standing sweep already
    /// covers. The carve-out now excludes any sibling site itself listed under
    /// <see cref="ReviewFindingDispositions.DoNotFixHere"/>, so a routed-away sibling stays
    /// routed away no matter which finding's sweep surfaces it.
    /// </para>
    /// <para>
    /// Cycle 11 review found the cycle-10 carve-out still one-directional: it excluded a sibling
    /// listed under <see cref="ReviewFindingDispositions.DoNotFixHere"/>, but a sibling separately
    /// dispositioned <see cref="ReviewFindingDispositions.FixHereInItsOwnCommit"/> was excluded
    /// only when the finding *being swept* carried that same disposition — an ordinary
    /// <see cref="ReviewFindingDispositions.FixHere"/> finding's sweep still told the sweeping
    /// session to merely name such a sibling, contradicting the disposition already recorded for
    /// it three paragraphs earlier. The carve-out is now keyed on the sibling site's own
    /// disposition alone, not on how the finding being swept is itself dispositioned: an explicit
    /// disposition on the sibling always wins, regardless of which finding's sweep surfaced it.
    /// The same edit folded the ambiguous "for every other pre-existing site" clause into an
    /// explicit "one this document does not separately disposition" test, closing the misreading
    /// where it could parse as excluding only a routed-away sibling rather than any dispositioned
    /// one.
    /// </para>
    /// <para>
    /// Cycle 12 review found the cycle-11 rewrite went too far: keying the carve-out on the
    /// sibling site's own disposition alone silently dropped the cycle-3 trigger the rewrite was
    /// supposed to be layering on top of, not replacing — an undispositioned sibling of a finding
    /// itself dispositioned <see cref="ReviewFindingDispositions.FixHereInItsOwnCommit"/> fell
    /// back to named-not-fixed, exactly the cea5ae6e <c>CreateAsync</c> outcome the cycle-3 ruling
    /// exists to prevent. Both keys now coexist: an explicit disposition on the sibling site
    /// itself still wins outright (cycle 10, cycle 11), and an undispositioned sibling of a
    /// <see cref="ReviewFindingDispositions.FixHereInItsOwnCommit"/> finding is fixed in that same
    /// separate commit (cycle 3), falling through to named-not-fixed only when neither applies.
    /// </para>
    /// <para>
    /// The foreground-test sub-rule's own timeout figure no longer restates the fixed 590-600
    /// second number the 2026-09-01 transcript mining paragraph above describes: that number was
    /// near-maximum only against the stock 10-minute <c>BASH_MAX_TIMEOUT_MS</c> in effect at the
    /// time (PLAN.md §16 #113), and <c>ClaudeSettingsFile</c> began overriding that stock cap on
    /// 2026-09-02, sizing it instead from the live <see cref="DaemonOptions.VerifyGateTimeout"/> —
    /// 60 minutes today. A fix session that took the stale figure literally had its `dotnet test`
    /// killed well inside this project's own 8-to-12-minute suite, the exact failure this whole
    /// rule exists to prevent (independent pre-PR review, cycle 3, conformance lens). The sub-rule
    /// now reads <see cref="WorkPromptBuilder.ForegroundCeilingMinutes"/> off the same
    /// <paramref name="commandTimeout"/> the prompt's own opening rule
    /// (<see cref="AppendSessionEndsAtFinalMessageRule"/>) already rendered, so the two statements
    /// of the ceiling in one prompt can never disagree.
    /// </para>
    /// </summary>
    private static void AppendReviewFixSelfCheckPhaseRules(
        StringBuilder prompt, ProjectDetails project, string effectiveBaseBranch,
        TimeSpan commandTimeout, string? stackedForkPointCommit = null)
    {
        // The line the sweep draws its own-changes boundary at. A stacked child names its recorded
        // fork point as a literal commit for the same reason every other stacked instruction does:
        // `origin/<parent>` is another task's branch, and a force-push there moves it out from
        // under the range, so a sweep taken against it reads the parent's rewritten-away delta as
        // this branch's own changes — and then fixes it here (conformance review, cycle 4).
        const string file = $"{TemplateDirectory}/review-fix-self-check.md";
        string sweepBoundary = stackedForkPointCommit ?? $"origin/{effectiveBaseBranch}";
        AppendFragment(prompt, file, "self-check-lead");
        AppendFragment(prompt, file, "class-sweep-lead", ("DoNotFixHere", ReviewFindingDispositions.DoNotFixHere));
        if (stackedForkPointCommit is not null)
        {
            AppendFragment(
                prompt, file, "draw-line-stacked",
                ("SweepBoundary", sweepBoundary), ("EffectiveBaseBranch", effectiveBaseBranch));
        }
        else
        {
            AppendFragment(prompt, file, "draw-line-unstacked", ("EffectiveBaseBranch", effectiveBaseBranch));
        }

        AppendFragment(
            prompt, file, "site-classification",
            ("SweepBoundary", sweepBoundary),
            ("FixHereInItsOwnCommit", ReviewFindingDispositions.FixHereInItsOwnCommit),
            ("DoNotFixHere", ReviewFindingDispositions.DoNotFixHere),
            ("FixHere", ReviewFindingDispositions.FixHere),
            ("RideAlong", ReviewFindingDispositions.RideAlong));
        AppendFragment(prompt, file, "regression-comparison");
        if (project.VerifyCommands.Count == 0)
        {
            AppendFragment(prompt, file, "no-tests");
        }
        else
        {
            AppendFragment(prompt, file, "run-touched-tests-lead");
            foreach (VerifyCommand gate in project.VerifyCommands)
            {
                prompt.AppendLine($"     - `{gate.Command}`");
            }

            int foregroundCeilingMinutes = WorkPromptBuilder.ForegroundCeilingMinutes(commandTimeout);
            AppendFragment(
                prompt, file, "foreground-timeout-note",
                ("ForegroundCeilingMinutes", foregroundCeilingMinutes.ToString(CultureInfo.InvariantCulture)),
                ("DeliverWord", "deliver"));
        }
        AppendFragment(prompt, file, "session-not-done");
    }

    /// <summary>
    /// The fan-in synthesis session (Decisions Log #36): when a claimed task has more
    /// immediate blockers than the node's threshold, this session condenses their handoffs
    /// into the one context document the build session actually reads. It is a platform
    /// dispatch like the reviewer — recorded model, recorded tokens, artifacts in the
    /// dependent run's own directory — and, like the reviewer, strictly read-only.
    /// <para>
    /// The instruction is to condense, never to judge: dropping a gotcha because it looked
    /// minor would defeat the whole point of routing it, and this session knows less about
    /// the dependent's work than the dependent will.
    /// </para>
    /// <para>
    /// Runs in the same worktree the dependent build session is about to claim
    /// (<c>BlockerContextAssembler.SynthesizeOrFallBackAsync</c>), and its own wait site already
    /// calls <c>TerminateTree</c> on this session once its result arrives, exactly like every
    /// other headless leg — so it carries the foreground-gates rule too
    /// (<see cref="AppendForegroundGatesRule"/>, <c>sessionRunsGates: false</c>): a session that
    /// backgrounds a command here and ends its turn is left waiting on a notification that can
    /// never arrive, the same as a review pass or a build session would be (independent pre-PR
    /// review, cycle 3, adversarial lens).
    /// </para>
    /// </summary>
    public static string BuildContextSynthesis(
        TaskDetails task, int blockerCount, string blockerContext, TimeSpan? commandTimeout = null)
    {
        const string file = $"{TemplateDirectory}/context-synthesis.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro", ("BlockerCount", blockerCount.ToString(CultureInfo.InvariantCulture)));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "task-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();
        if (task.AcceptanceCriteria.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "acceptance-criteria-heading"));
            foreach (string criterion in task.AcceptanceCriteria)
            {
                prompt.AppendLine($"- {criterion}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "handoffs-heading"));
        prompt.AppendLine();
        prompt.AppendLine(blockerContext);
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "how-to-condense-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "merge-overlaps");
        AppendFragment(prompt, file, "keep-gotchas");
        AppendFragment(prompt, file, "keep-attribution");
        AppendFragment(prompt, file, "say-only-what-handoffs-say");
        AppendFragment(prompt, file, "handoffs-inform-not-instruct");
        AppendFragment(prompt, file, "read-only-note");
        AppendForegroundGatesRule(
            prompt, commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout, sessionRunsGates: false);
        AppendExternalInteractionLoggingRule(prompt, task.Id);
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "output-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "output-body", ("BlockerContextHeading", BlockerContextDocument.Heading));

        return prompt.ToString();
    }

    /// <summary>
    /// The prompt for a card-publication session (backlog 18): compose this task as a card in
    /// Jira, then submit the composed payload through the surface that actually writes it.
    /// <para>
    /// What this prompt deliberately does not contain is any instruction about what a card should
    /// look like — no issue type, no field list, no routing rule. That is the whole design: those
    /// are one organisation's Jira configuration, they are already written down in the teams that
    /// have them, and a platform that modelled them would be modelling somebody's admin screen and
    /// then arguing with it. So the session runs in the project's repository where its own skills
    /// are, is pointed at them, and is otherwise told what the work is and left to it.
    /// </para>
    /// <para>
    /// The ending is the part that is not left open, and it changed shape with the compose/execute
    /// split (Brian's design, 2026-08-28): the session performs no direct Jira access at all. It
    /// finishes by running <c>h9k task write-jira --op create</c> with its composed payload, and
    /// that command — never the agent — validates it, executes it against the Jira Cloud REST API,
    /// and reads the key back before recording anything, so the prompt says outright that composing a payload is not
    /// the same as the platform believing a card exists, and that a refusal from that command is
    /// information to act on rather than a wall. An agent that understands the gate retries against
    /// it correctly; one that does not would report success into a void.
    /// </para>
    /// </summary>
    public static string BuildCardPublication(
        TaskDetails task,
        ProjectDetails project,
        string workingDirectory,
        string site,
        JiraProjectKey board,
        string writeCommand,
        string? routingGuidance = null)
    {
        const string file = $"{TemplateDirectory}/card-publication.md";
        StringBuilder prompt = new();
        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "intro", ("Site", site));
        prompt.AppendLine();

        prompt.AppendLine(PromptTemplates.Load(file, "work-heading"));
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (task.AcceptanceCriteria.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "acceptance-criteria-heading"));
            prompt.AppendLine();
            foreach (string criterion in task.AcceptanceCriteria)
            {
                prompt.AppendLine($"- {criterion}");
            }

            prompt.AppendLine();
        }

        if (task.AgentContext.IsNotBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "context-heading"));
            prompt.AppendLine();
            prompt.AppendLine(task.AgentContext);
            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "where-it-goes-heading"));
        prompt.AppendLine();
        prompt.AppendLine(board.HasValue
            ? Fragment(file, "bound-to-board", ("ProjectName", project.Name), ("Board", board.Value))
            : Fragment(file, "no-board-bound", ("ProjectName", project.Name)));
        prompt.AppendLine();

        // Free text, handed over exactly as the project recorded it (h9k project set
        // --backlog-routing) rather than parsed: an agent can read "epic-first, ask for the
        // parent before filing" the way a deterministic github-issues author never could, which
        // is the whole reason this policy dispatches a session at all.
        if (routingGuidance.IsNotBlank())
        {
            prompt.AppendLine(Fragment(file, "routing-guidance", ("RoutingGuidance", routingGuidance)));
            prompt.AppendLine();
        }

        AppendFragment(prompt, file, "no-modeling");
        prompt.AppendLine();

        IReadOnlyList<RepoSkill> skills = DiscoverRepoSkills(workingDirectory);
        if (skills.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "repo-skills-heading"));
            foreach (RepoSkill skill in skills)
            {
                prompt.AppendLine(skill.Description is null
                    ? $"- `{skill.Name}`"
                    : $"- `{skill.Name}` — {skill.Description}");
            }

            prompt.AppendLine();
        }

        // Card-authoring rules are exactly the kind of thing that lives one tier out from the
        // repository — a team's conventions for a board, not for a codebase — so the home's
        // skills are named here as well as the repo's.
        IReadOnlyList<RepoSkill> homeSkills = [.. DiscoverHomeSkills(project)
            .Where(skill => !skills.Any(repo => repo.Name == skill.Name))];
        if (homeSkills.Count > 0)
        {
            prompt.AppendLine(Fragment(file, "home-skills-heading", ("HomeDirectory", project.HomeDirectory.Value)));
            foreach (RepoSkill skill in homeSkills)
            {
                prompt.AppendLine(skill.Description is null
                    ? $"- `{skill.Name}`"
                    : $"- `{skill.Name}` — {skill.Description}");
            }

            prompt.AppendLine();
        }

        if (project.ContextLinks.Count > 0)
        {
            prompt.AppendLine(PromptTemplates.Load(file, "project-links-heading"));
            prompt.AppendLine();
            foreach (ContextLink link in project.ContextLinks)
            {
                prompt.AppendLine($"- {link.Name}: {link.Url}");
            }

            prompt.AppendLine();
        }

        prompt.AppendLine(PromptTemplates.Load(file, "reporting-back-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "payload-shape-intro");
        prompt.AppendLine();
        AppendFragment(prompt, file, "payload-example");
        prompt.AppendLine();
        AppendFragment(prompt, file, "payload-fields-explained");
        prompt.AppendLine();
        AppendFragment(prompt, file, "submit-command", ("WriteCommand", writeCommand));
        prompt.AppendLine();
        AppendFragment(prompt, file, "payload-not-existence");
        prompt.AppendLine();
        AppendFragment(prompt, file, "run-in-foreground");
        prompt.AppendLine();

        prompt.AppendLine(PromptTemplates.Load(file, "working-rules-heading"));
        prompt.AppendLine();
        AppendFragment(prompt, file, "worktree-note", ("WorkingDirectory", workingDirectory));
        AppendFragment(prompt, file, "compose-once");
        AppendFragment(prompt, file, "card-audience");
        if (task.CurrentRunId is not null)
        {
            // Nothing about card publication gates on task state (TaskDecider.RequestWorkItemPublication
            // refuses only Abandoned; CardPublicationEngine selects purely on a pending request), so a
            // publication session dispatched against a Claimed task — `push-to-jira` run on a Working
            // task, or the request appended alongside `task publish --assign` on a jira-backlog project
            // — has a live run exactly like any other dispatched prompt. Asserting otherwise here
            // would tell a session in that case the invariant does not apply when `h9k task
            // log-interaction` would in fact succeed (independent pre-PR review, cycle 1).
            AppendExternalInteractionLoggingRule(prompt, task.Id);
        }
        else
        {
            AppendFragment(prompt, file, "no-logging-invariant");
        }
        AppendAdoptedContextRule(prompt, task);
        AppendFragment(prompt, file, "cannot-create-card");
        AppendFragment(prompt, file, "closing-summary");

        return prompt.ToString();
    }

}
