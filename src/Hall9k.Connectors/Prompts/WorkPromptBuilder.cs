using System.Globalization;
using System.Linq;
using System.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The build/work prompt (PLAN.md §4): objective, acceptance criteria, agent context, the
/// project's context links, and — when the worktree ships repo skills — a one-line pointer per
/// skill. Extracted out of <c>Hall9k.Daemon.Execution.AgentPromptBuilder</c> so headless dispatch
/// (<c>RunLauncher</c>) and an operator's interactive claim (<c>h9k task work</c>) assemble the
/// prompt through the identical code — the CLI cannot reference <c>Hall9k.Daemon</c> (the CLI
/// never hosts Wolverine), so this is the shared home both sides call. The daemon's own
/// AgentPromptBuilder pulls this in with a <c>using static</c> so its follow-up/review prompt
/// builders keep calling these helpers unqualified.
/// </summary>
public static class WorkPromptBuilder
{
    /// <summary>The package this builder's own prose ships under in <c>.claude/templates</c>.</summary>
    public const string TemplateDirectory = "work-prompt-builder";

    // The CLI's own command-name registrations (Hall9k.Cli.Infrastructure.CliCommandTree) and the
    // RESOLUTION: value vocabulary a review verdict is parsed from — both listed in
    // PromptContractTokens.All, so PromptTemplateContractTests fails outright if any of these
    // literal words is typed into a template file directly. A template that needs one in the
    // rendered prompt gets it as a substituted {{...}} parameter instead, computed here once.
    private const string DeliverWord = "deliver";
    private const string RegisterSessionWord = "register-session";
    private const string MergeReadyWord = "merge-ready";
    private const string NeedsFixesWord = "needs-fixes";

    /// <summary>
    /// The commit a stacked session's own fork point is named by literally, or null when naming a
    /// ref is safe (independent pre-PR review, cycle 1, adversarial lens). A parent branch is not
    /// like the project's base: it is routinely force-pushed while the child builds — an
    /// absorb-review-fixes lap folding fixes into its own commits — which rewrites the history the
    /// child shares with it, so <c>git merge-base origin/&lt;parent&gt; HEAD</c> collapses BELOW
    /// this branch's real fork point and the recompose's mixed reset would dissolve the parent's
    /// commits along with this session's own, recomposing the parent's work as this branch's
    /// authored history. The tree-identity check cannot catch that (a mixed reset never moves the
    /// tree), which is exactly what PLAN.md §16 #144 names as the reason the fork point has to be
    /// the recorded commit — <c>RunDispatched.BaseCommit</c>, observed at the cut.
    /// <para>
    /// Null for every ordinary run, which keeps every unstacked prompt byte-identical, and null for
    /// a stacked run whose fork point was never observed (a resumed worktree, an unreadable
    /// rev-parse, a stream written before the field): there is no recorded commit to name, so the
    /// merge-base wording stands as the best available answer rather than a guessed commit
    /// (AGENTS.md's never-guess rule).
    /// </para>
    /// <para>
    /// Public because the same discriminator answers the same question for every prompt that tells
    /// a session to rewrite this branch's history, not just a build session's own recompose:
    /// <c>AgentPromptBuilder</c>'s follow-up prompts read it for their rebase and
    /// fixup-autosquash instructions, which are wrong against a force-pushed parent branch for the
    /// identical reason (independent pre-PR review, cycle 2, adversarial lens).
    /// </para>
    /// </summary>
    public static string? StackedForkPoint(ProjectDetails project, string effectiveBaseBranch, string? baseCommit) =>
        effectiveBaseBranch != project.BaseBranch && baseCommit.IsNotBlank() ? baseCommit : null;

    public static string Build(
        TaskDetails task,
        ProjectDetails project,
        string branch,
        string worktreePath,
        bool resumesPreviousWork = false,
        string? blockerContext = null,
        string? resumeReason = null,
        bool isInteractive = false,
        bool isHandback = false,
        bool isDeliberateHeadlessStart = false,
        bool requiresSelfRegistration = false,
        string? interactiveMilestoneAddress = null,
        bool isDelegatedContractor = false,
        string? delegationNote = null,
        string? delegationBaseCommit = null,
        string? baseBranch = null,
        string? baseCommit = null,
        TimeSpan? commandTimeout = null)
    {
        // The branch this session's work sits on top of, resolved by the caller at dispatch
        // (RunDispatched.BaseBranch): the project's own for every ordinary run, a stacked child's
        // parent branch instead (task: a stacked pull-request edge exists as an explicit opt-in
        // dependency). Every base-branch reference this prompt makes — the self-review phase's own
        // diff range and the recompose's fork point above all — reads this rather than the
        // project's, so a stacked child's session reviews and recomposes its own delta rather than
        // its parent's work alongside it. Null defers to the project's, which is what every caller
        // with no run to read one from passes.
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        string? stackedForkPointCommit = StackedForkPoint(project, effectiveBaseBranch, baseCommit);
        // The live ceiling this session's own settings file actually enforces when a caller with
        // one in reach (ClaudeExecutor, via RunLauncher) passes it through; the CLI's own
        // h9k task work claim structurally cannot reach DaemonOptions (Reference graph: Cli ->
        // Domain + Connectors), so it falls back to the same constant ClaudeSettingsFile.Build
        // itself falls back to for that caller (independent pre-PR review, cycle 1, both lenses).
        TimeSpan effectiveCommandTimeout = commandTimeout ?? ClaudeSettingsFile.DefaultCommandTimeout;
        const string file = $"{TemplateDirectory}/build.md";
        StringBuilder prompt = new();
        AppendFragment(prompt, file, "title");
        prompt.AppendLine();
        prompt.AppendLine(task.Objective);
        prompt.AppendLine();

        if (isDelegatedContractor)
        {
            AppendDelegatedContractorSection(prompt, resumesPreviousWork, delegationNote);
        }
        else if (resumesPreviousWork && isHandback)
        {
            // Unlike the causeless branch below, this one is not a guess: TaskDetails.ResumesFromHandback
            // is set only from TaskHandedBack, so this run is dispatching because a human's own
            // h9k task work claim was handed back (h9k task handback) — an observed fact, not the
            // ambiguity the causeless wording exists to avoid asserting past.
            AppendFragment(prompt, file, "handback-heading");
            prompt.AppendLine();
            AppendFragment(prompt, file, "handback-body");
            if (resumeReason.IsNotBlank())
            {
                prompt.AppendLine();
                AppendFragment(prompt, file, "handback-reason", ("ResumeReason", resumeReason));
            }

            prompt.AppendLine();
        }
        else if (resumesPreviousWork)
        {
            // This branch resumes a retained worktree, and the retained worktree carries
            // whatever the prior attempt left — including uncommitted work (origin incident,
            // 2026-08-18: gen 2-4 of a review-parked task each rebuilt the same feature
            // from scratch instead of finding the finished work already in the worktree).
            // Worded without a cause on purpose: this same flag is true for a genuine failure
            // retry (h9k task retry) and an operator simply re-entering their own still-open
            // interactive claim (h9k task work) — asserting "a previous attempt failed" here
            // would be exactly the unobserved-fact guess AGENTS.md forbids on the feature's
            // own headline path (adversarial review, cycle 1). A handback (h9k task handback)
            // is known rather than guessed, so it gets the more specific branch above instead.
            AppendFragment(prompt, file, "resume-causeless-heading");
            prompt.AppendLine();
            AppendFragment(prompt, file, "resume-causeless-body");
            prompt.AppendLine();
        }

        // Reachable regardless of which branch above ran, or none of them (independent pre-PR
        // review, cycle 1, adversarial lens): the operator's retry reason is about the attempt,
        // not about whether an old branch happened to survive to resume — a retry that starts
        // clean because the branch is gone still carries a reason just as live as one that
        // resumes. Excluded only where the reason already has its own, more specific rendering:
        // isDelegatedContractor's own delegationNote is unrelated to a retry, and the explicit
        // handback branch above already quoted resumeReason under "Why they handed it back".
        if (!isDelegatedContractor && !(resumesPreviousWork && isHandback))
        {
            // task.RetryPending gates this branch the same way it gates AppendOperatorGuidanceSection
            // below (independent pre-PR review, cycle 3, both lenses): a handback's own reason
            // survives a later TaskCompleted/TaskResolved/TaskAbandoned on this same field
            // (RetryReasonIsHandback's own doc), so without this check a long-settled attempt's
            // handback note would be presented as "why this run resumes here" on a run that
            // resumes nothing of the sort — the same stale-reason failure mode RetryPending was
            // introduced to close off on the operator-guidance path.
            if (task.RetryReasonIsHandback && task.RetryReason.IsNotBlank() && task.RetryPending)
            {
                // This dispatch did not take the explicit handback branch above — isHandback is
                // false, whether because this caller never passes it (h9k task work) or because
                // TaskRequeued severed ResumesFromHandback after an earlier headless attempt on
                // this same handback died — but the standing reason is still a handback's own
                // words, not a retry instruction, so it keeps the same causeless wording that
                // branch would have used rather than being mislabeled as operator guidance
                // (independent pre-PR review, cycle 1, both lenses).
                AppendFragment(prompt, file, "retry-reason-is-handback-causeless", ("RetryReason", task.RetryReason));
                prompt.AppendLine();
            }
            else
            {
                AppendOperatorGuidanceSection(prompt, task);
            }
        }

        AppendFragment(prompt, file, "acceptance-criteria-heading");
        prompt.AppendLine();
        foreach (string criterion in task.AcceptanceCriteria)
        {
            prompt.AppendLine($"- {criterion}");
        }

        prompt.AppendLine();

        if (task.AgentContext.IsNotBlank())
        {
            AppendFragment(prompt, file, "context-heading");
            prompt.AppendLine();
            prompt.AppendLine(task.AgentContext);
            prompt.AppendLine();
        }

        // What this task's immediate blockers handed down (Decisions Log #36). It sits after
        // the task's own context and before the project links: nearer than a link the agent
        // may or may not fetch, and never mistaken for part of the objective.
        if (blockerContext.IsNotBlank())
        {
            prompt.AppendLine(blockerContext);
            prompt.AppendLine();
        }

        if (project.ContextLinks.Count > 0)
        {
            AppendFragment(prompt, file, "project-links-heading");
            prompt.AppendLine();
            foreach (var link in project.ContextLinks)
            {
                AppendFragment(prompt, file, "project-links-line", ("Name", link.Name), ("Url", link.Url.ToString()));
            }

            prompt.AppendLine();
        }

        AppendProjectHome(prompt, project);

        AppendFragment(prompt, file, "working-rules-heading");
        prompt.AppendLine();
        if (requiresSelfRegistration)
        {
            // Unlike a direct launch or a headless dispatch, neither of which reaches this
            // prompt without their process's own WorkingDirectory already set to worktreePath,
            // the prompt-handoff default (h9k task work's own doc: "paste into a session started
            // anywhere") gives no guarantee this session's cwd is the worktree at all — asserting
            // "you are in" it here would be a false claim about a location this session was never
            // actually placed in (independent pre-PR review, cycle 1, both lenses).
            AppendFragment(prompt, file, "worktree-self-registration",
                ("WorktreePath", worktreePath), ("Branch", branch));
        }
        else
        {
            AppendFragment(prompt, file, "worktree-plain", ("Branch", branch));
        }

        AppendFragment(prompt, file, "implement-objective");
        AppendFragment(prompt, file, "commit-clear-messages");
        if (isInteractive)
        {
            AppendFragment(prompt, file, "interactive-delivery-line", ("Deliver", DeliverWord));
            AppendCommitDisciplineRuleForInteractiveSession(prompt);
            AppendSelfDeliveryRule(prompt);
            // The take-the-wheel session composes no pull request body of its own — that is why it
            // never reaches AppendPullRequestSummaryStep — but it writes commit messages the
            // operator pushes, and it drafts comments and replies they post under their own login.
            // The conventions reach it here or not at all (task 412afe6c).
            AppendWritingConventions(
                prompt, string.Empty, project.WritingConventions,
                PromptTemplates.Load(file, "interactive-writing-conventions-lead"));
            if (requiresSelfRegistration)
            {
                AppendSelfRegistrationRule(prompt, task.Id);
                AppendFindLiveAgentsRule(prompt, task.Id);
                AppendPlatformSettingsReminderRule(prompt, project);
            }
        }
        else if (isDelegatedContractor)
        {
            // The same unsupervised framing isDeliberateHeadlessStart's own branch below states,
            // for the identical reason (this contractor's RunDispatched also carries the
            // ceiling-exempt Guid.Empty NodeId) — but the commit rules that follow diverge from
            // that branch's on purpose (adversarial review, cycle 1, TaskDelegateCommand.cs:367):
            // this worktree can already hold the operator's own authored commits, made on their
            // own live interactive claim before this delegation, and the generic recompose below
            // resets to the branch's fork point against origin/{baseBranch} — which would treat
            // those commits as fair game to rewrite right alongside this contractor's own, the
            // opposite of "respect what is already here by default" two sections up.
            AppendFragment(prompt, file, "delegated-contractor-intro", ("Deliver", DeliverWord));
            AppendDelegatedContractorCommitRules(
                prompt, project, worktreePath, delegationBaseCommit, effectiveBaseBranch,
                stackedForkPointCommit);
            AppendSessionEndsAtFinalMessageRule(prompt, effectiveCommandTimeout);
        }
        else if (isDeliberateHeadlessStart)
        {
            // RunSupervisor.AdoptDeliberateHeadlessStartsAsync now watches this run too (task: a
            // do-now session launched by h9k task start is caught within seconds) — the moment
            // this session exits, the platform reads the worktree itself and either delivers a
            // clean, committed tree automatically or flags anything else for a human, so it is no
            // longer true that nothing supervises this run. What is still true, and still framed
            // as a human's act rather than this session's own: verification and delivery are never
            // this session's to trigger by hand.
            // InteractiveSessionLiveness.EnsureNotAttachedElsewhere refuses both h9k task deliver
            // and h9k task verify unconditionally from inside this very session (unlike an attached
            // h9k task work claim, verify's self-invocation exemption keys on
            // HALL9K_INTERACTIVE_RUN_ID, which HeadlessLaunch.SpawnDetached never sets), so an
            // instruction telling this session to run either itself describes a command that always
            // fails (conformance and adversarial review, cycle 4).
            AppendFragment(prompt, file, "deliberate-headless-start-intro", ("Deliver", DeliverWord));
            AppendCheckpointCommitRules(
                prompt, project, worktreePath, effectiveBaseBranch, stackedForkPointCommit);
            AppendSessionEndsAtFinalMessageRule(prompt, effectiveCommandTimeout);
        }
        else
        {
            AppendFragment(prompt, file, "headless-dispatch-line");
            AppendCheckpointCommitRules(
                prompt, project, worktreePath, effectiveBaseBranch, stackedForkPointCommit);
            AppendSessionEndsAtFinalMessageRule(prompt, effectiveCommandTimeout);
        }

        IReadOnlyList<RepoSkill> skills = DiscoverRepoSkills(worktreePath);
        if (skills.Count > 0)
        {
            AppendFragment(prompt, file, "skills-heading");
            foreach (RepoSkill skill in skills)
            {
                AppendSkillLine(prompt, skill);
            }
        }

        AppendHomeSkillRule(prompt, project, skills);
        AppendExternalInteractionLoggingRule(prompt, task.Id);

        AppendAdoptedContextRule(prompt, task);
        AppendBlockerContextRule(prompt, blockerContext);
        AppendFragment(prompt, file, isInteractive ? "ambiguous-interactive" : "ambiguous-headless");

        AppendFragment(prompt, file, "end-summary");

        // Last, not immediately after AppendExternalInteractionLoggingRule (independent pre-PR
        // review, cycle 1, both lenses): this method opens its own "##" heading, so calling it
        // mid-list nested every rule appended after it under "Reporting to the human" instead of
        // under "## Working rules". The live-attended build (isInteractive) is the human's own
        // session — there is nobody else here for it to report to, so R8's outbound milestones
        // apply only to a headless build dispatched under interactive mode (h9k task start, or an
        // ordinary dispatch carrying the flag forward from an earlier h9k task release
        // --keep-interactive — never a handback, which clears the flag unconditionally).
        if (task.InteractiveModeEnabled && !isInteractive)
        {
            // parksAtBoundaryAfterward left at its true default here (independent pre-PR review,
            // cycle 3, conformance lens): a deliberate headless start (h9k task start) used to
            // pass false, on the premise that "nothing supervises this run once you end" — but
            // RunSupervisor.AdoptDeliberateHeadlessStartsAsync now does (task: a do-now session
            // launched by h9k task start is caught within seconds), delivering a clean, committed
            // tree automatically and, since this branch is only ever reached with
            // task.InteractiveModeEnabled true, the review loop's own first boundary then parks
            // for the human exactly as it would for any other interactive-mode build. Passing
            // false here told this exact session the opposite of what the "## Working rules"
            // section above it already says.
            AppendOutboundMilestoneRules(
                prompt, "build", OutboundMilestone.Build, interactiveMilestoneAddress,
                isDelegatedContractor: isDelegatedContractor);
        }
        else if (task.InteractiveModeEnabled)
        {
            // The attended half of the same contract (task: a human at the wheel takes the fix role
            // herself, fourth criterion). A headless dispatch above is told how to REPORT to the human; the
            // session they are sitting at is told what they can CHOOSE, so it can offer them the four
            // choices at the review-verdict-to-fix boundary — their own fix among them — in words.
            // Last for the same reason the call above is: it opens its own "##" heading, so placing
            // it mid-list would nest every rule after it underneath.
            AppendInteractiveBoundaryChoices(prompt, task.Id);
        }

        if (!isInteractive)
        {
            AppendHandoffRules(prompt);
        }

        return prompt.ToString();
    }

    /// <summary>
    /// The operator's own words at retry time (task: a headless retry's reason reaches the
    /// resumed session), rendered under one clearly-labeled heading wherever a run dispatches
    /// straight out of <c>h9k task retry --reason</c>: the resumed-build branch above, and each
    /// of <c>Hall9k.Daemon.Execution.AgentPromptBuilder</c>'s follow-up prompts (review feedback,
    /// failing checks, rebase), which call this same helper through the <c>using static</c> import
    /// that already shares the rest of this type's rendering helpers with that file.
    /// <para>
    /// Origin incident (2026-09-06): <see cref="TaskDetails.RetryReason"/> reached only
    /// <c>h9k task show</c> and the interactive CLI's own prompt calls — nothing under
    /// <c>src/Hall9k.Daemon</c> read it, so five stale tasks retried with "rebase onto origin/main
    /// first" each ran their ordinary follow-up template, found nothing to do, and failed the
    /// same gate again.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Skipped when the field is blank (the task has never been retried), when
    /// <see cref="TaskDetails.RetryReasonIsHandback"/> is set — that flag means this same field's
    /// text was last written by a handback (<c>h9k task handback</c>), not a retry, and rendering
    /// it here under "operator guidance" would mislabel a hand-off note as a retry instruction —
    /// when <see cref="TaskDetails.RetryPending"/> is false, because the retry this text answers
    /// for has already been superseded by a completion (or resolve, or abandon) with no new retry
    /// since, so presenting it as "what to prioritize for THIS run" would hand a long-settled
    /// attempt's own reason to a run working on something else entirely (independent pre-PR
    /// review, cycle 1, both lenses), and when the text is exactly
    /// <see cref="TaskDecider.DefaultRetryReason"/> — the CLI's own honest filler for a bare
    /// <c>h9k task retry</c> with no <c>--reason</c>, which states that a retry happened but
    /// asserts nothing about what to prioritize, so rendering it under a heading that promises "a
    /// human gave this instruction" would be exactly the unobserved-fact guess AGENTS.md forbids
    /// (independent pre-PR review, cycle 1, conformance lens).
    /// </remarks>
    public static void AppendOperatorGuidanceSection(StringBuilder prompt, TaskDetails task)
    {
        if (task.RetryReason.IsBlank()
            || task.RetryReasonIsHandback
            || !task.RetryPending
            || task.RetryReason == TaskDecider.DefaultRetryReason)
        {
            return;
        }

        const string file = $"{TemplateDirectory}/operator-guidance.md";
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "lead");
        prompt.AppendLine();
        prompt.AppendLine(task.RetryReason);
        prompt.AppendLine();
    }

    /// <summary>
    /// <c>h9k task delegate</c>'s own framing (design ruling R6, idea fcaded0b's design rulings,
    /// Take the Wheel epic 9272e514's slice 10): distinct from both branches above on purpose.
    /// Unlike a handback, this is not a permanent hand-off — the operator stays the arbiter and
    /// reads the contractor's report before deciding anything, including re-entering this very
    /// worktree with <c>h9k task work</c> to finish by hand. Unlike the causeless "a previous
    /// attempt worked here first" branch, this always has a real, current author to name: the
    /// operator holding this task interactively right now. <paramref name="delegationNote"/> is
    /// their own handoff in the blocker-handoff mold (what was attempted, what is deliberate
    /// versus abandoned, what latitude is granted) — trusted instruction from the task's own
    /// arbiter, not foreign text needing the data-only boundary <see cref="AppendBlockerContextRule"/>
    /// gives an adopted issue's own words, so it is quoted verbatim with nothing hedging it.
    /// <para>
    /// The conservative default (design ruling R6's own closing line: "the prompt's default for
    /// inherited work stays conservative") is stated unconditionally, whether or not the branch is
    /// virgin: a contractor dispatched onto a clean worktree still needs to hear it, since a
    /// second delegation on the same claim can follow the first one's own commits.
    /// </para>
    /// </summary>
    private static void AppendDelegatedContractorSection(StringBuilder prompt, bool resumesPreviousWork, string? delegationNote)
    {
        const string file = $"{TemplateDirectory}/delegated-contractor-section.md";
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "lead");
        prompt.AppendLine();
        AppendFragment(prompt, file, resumesPreviousWork ? "resuming" : "virgin");

        prompt.AppendLine();
        AppendFragment(prompt, file, "respect-existing-work");
        prompt.AppendLine();
        AppendFragment(prompt, file, "handoff-note-lead");
        prompt.AppendLine();
        foreach (string line in (delegationNote ?? string.Empty).Split('\n'))
        {
            prompt.AppendLine($"> {line.TrimEnd('\r')}");
        }

        prompt.AppendLine();
    }

    /// <summary>
    /// The data-only boundary around an adopted item's description (PLAN.md §3.1a). For a task
    /// somebody adopted, the Context section is a quoted issue body, and anyone who can file an
    /// issue in that repo wrote it — so it can say "ignore the acceptance criteria" as easily as
    /// it can describe a bug. <c>WorkItemContext</c> frames and fences it; this is the other
    /// half, and the half that holds: the working rules are the last section in the prompt and
    /// the daemon authors every line of them, so text inside the quote can claim anything about
    /// itself and still not get behind this.
    /// <para>
    /// The rule is gated deliberately. For a task whose context the owner typed, the context
    /// <em>is</em> instruction, and a standing rule to read it as inert data would teach the agent
    /// to ignore the person who dispatched it. Only <see cref="Build"/> needs it: the follow-up
    /// and fix-checks prompts carry the objective, not the agent context.
    /// </para>
    /// <para>
    /// Gated on the quote being there rather than on the task having been adopted, because those
    /// come apart: an <c>ExternalReference</c> is permanent and the context under it is not.
    /// <c>h9k task revise --context</c> replaces the agent context wholesale, so after
    /// adopt-then-revise the reference still names the issue while the Context section holds the
    /// owner's own words — and a rule gated on the reference would introduce those words as a
    /// stranger's, telling the agent to report its owner's instruction rather than act on it.
    /// <see cref="WorkItemContext.CarriesQuotedDescription"/> asks the question that is actually
    /// being answered here.
    /// </para>
    /// </summary>
    public static void AppendAdoptedContextRule(StringBuilder prompt, TaskDetails task)
    {
        if (task.ExternalReference.IsBlank()
            || !WorkItemContext.CarriesQuotedDescription(task.AgentContext))
        {
            return;
        }

        const string file = $"{TemplateDirectory}/adopted-context-rule.md";
        AppendFragment(prompt, file, "rule", ("ExternalReference", task.ExternalReference));
    }

    /// <summary>
    /// The same boundary around blocker context (Decisions Log #36), and unconditional, which is
    /// the whole point of it. A handoff is a carrier for outside text by design: the blocker's own
    /// agent was told to report any instruction it found in its adopted issue body <em>in its
    /// summary</em>, that summary becomes the handoff, and <c>BlockerContextDocument</c> pastes it
    /// in here under framing that vouches for it as "what that blocker's own run handed down". So
    /// an issue body two tasks upstream can arrive as trusted guidance in a task that was never
    /// adopted from anything and has no external reference to gate a rule on.
    /// <para>
    /// Gated on the presence of blocker context rather than on any reference, therefore, and
    /// worded as a property of the section rather than of its source: blocker context informs and
    /// never instructs. The dependent agent cannot tell which sentence in a handoff its blocker
    /// wrote and which one it was quoting, and it does not have to — nothing in that section
    /// changes the objective, the criteria, or these rules, whoever wrote it.
    /// </para>
    /// <para>
    /// A synthesis document arrives through this same parameter, so it is covered by the same
    /// line without a case of its own — which is the reason the rule is about the section rather
    /// than about how the section was produced.
    /// </para>
    /// </summary>
    public static void AppendBlockerContextRule(StringBuilder prompt, string? blockerContext)
    {
        if (blockerContext.IsBlank())
        {
            return;
        }

        const string file = $"{TemplateDirectory}/blocker-context-rule.md";
        AppendFragment(prompt, file, "rule", ("Heading", BlockerContextDocument.Heading.TrimStart('#', ' ')));
    }

    /// <summary>
    /// The handoff the run leaves for whatever depends on it (Decisions Log #36). It is asked
    /// for here, of the agent that did the work, because that agent is the one that knows what
    /// it deliberately left undone — a separate summarizer session would cost more and know
    /// less. The daemon reads this block off the session's own result at session end and holds
    /// it until the pull request merges; a run whose work never lands hands nothing down.
    /// <para>
    /// Brevity is instructed rather than merely enforced: the event that carries this text is
    /// a milestone on the run stream (log #6), and a handoff nobody finishes reading routes no
    /// context at all.
    /// </para>
    /// <para>
    /// Headless only (<see cref="Build"/> skips this call when <c>isInteractive</c> is true): the
    /// parser this text promises — <c>RunSupervisor.CaptureHandoffAsync</c> — reads a headless
    /// session's own <c>--output-format stream-json</c> result payload, which an attached
    /// interactive session never produces and no "final message" ever ends. An operator's own
    /// handoff is instead the one <c>TaskDeliverCommand.PromptForHandoff</c> asks for at delivery
    /// time (adversarial review, cycle 6).
    /// </para>
    /// </summary>
    public static void AppendHandoffRules(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/handoff-rules.md";
        prompt.AppendLine();
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "body");
        prompt.AppendLine();
        AppendFragment(prompt, file, "marker", ("HandoffMarker", HandoffParser.Marker));
        prompt.AppendLine();
        AppendFragment(prompt, file, "tail");
    }

    /// <summary>
    /// The adversarial self-review phase (task: the build session ends with an adversarial
    /// self-review loop): once the full suite is green and before the recompose, the session
    /// re-reads its own finished diff assuming it wrote a defect into it. Placed here, inside
    /// <see cref="AppendCheckpointCommitRules"/> and therefore only in the headless build path of
    /// <see cref="Build"/>, for the same scoping reason the checkpoint/recompose protocol itself is
    /// scoped there: a fix round resumes an existing PR branch and never runs this hunt.
    /// <para>
    /// Ordered after the suite is green and before the recompose on purpose: the hunt has to see
    /// finished work rather than a mid-flight diff, and the recompose has to compose the tree the
    /// hunt leaves behind, so nothing the hunt fixes is left out of the branch's real history.
    /// </para>
    /// <para>
    /// The two named failure classes are both origin incidents from one afternoon (2026-08-30):
    /// cea5ae6e's cycle 6 landed a reflog fix on one of two branch-creating arms, and b6dfcbe5's
    /// park found a two-escape cancellation finding with only one escape closed — both a
    /// blast-radius sweep by the author would have caught, and both instead cost a full external
    /// review lap to surface. The third hunt, executing rather than proofreading an authored
    /// procedure, is the same class the commit-plan skill's own unexecutable stash sequence was
    /// caught by only when a reviewer actually ran it.
    /// </para>
    /// <para>
    /// Deliberately narrow: no model change for this phase (the task's own experiment design
    /// keeps the build session on its existing model, so before/after review-cycle counts
    /// attribute to the prompt alone), and no expectation of catching the deep conjunction class
    /// same-context review inherits the author's own assumptions on — that stays the external
    /// reviewers' job.
    /// </para>
    /// </summary>
    /// <param name="recomposeFollows">
    /// True (the default) when a recompose step immediately follows this phase, so the wording may
    /// point ahead to it. <see cref="AppendDelegatedContractorCommitRules"/> passes false on its
    /// unreadable-base-commit path, where no recompose ever runs: pointing this phase's own wording
    /// at a step the caller then forbids reads as contradictory (independent pre-PR review, cycle
    /// 1, both lenses).
    /// </param>
    /// <param name="baseBranch">
    /// The branch the hunt's own diff range is taken against — the project's own for every ordinary
    /// run, a stacked child's parent branch instead, so a stacked session hunts its own delta rather
    /// than reading its parent's already-reviewed work as part of this branch. Null defers to the
    /// project's.
    /// </param>
    /// <param name="stackedForkPointCommit">
    /// The recorded fork point the round-one range is taken from for a stacked session — see
    /// <see cref="StackedForkPoint"/> for why the parent branch cannot be named as a ref here
    /// either: a parent force-pushed mid-session moves <c>origin/&lt;parent&gt;</c> out from under
    /// the range, folding the parent's rewritten delta into what the hunt reads as this branch's
    /// own work. Null (every ordinary run) keeps the range on the base branch exactly as it was.
    /// </param>
    public static void AppendSelfReviewPhaseRules(
        StringBuilder prompt, ProjectDetails project, string worktreePath, bool recomposeFollows = true,
        string? baseBranch = null, string? stackedForkPointCommit = null)
    {
        string effectiveBaseBranch = baseBranch ?? project.BaseBranch;
        // Suffixed with the worktree's own directory name (unique per session, since a node
        // dispatches each concurrent session into its own worktree) so two build sessions
        // running at once on the same node never clobber one another's round-one tip file.
        // Forward-slashed and quoted at every interpolation site below: the session runs this
        // command in a POSIX-shaped shell regardless of host OS, and Path.GetTempPath()'s
        // backslashes on Windows would otherwise be consumed as shell escapes, silently
        // collapsing the path and writing the tip file inside the worktree instead of outside it
        // (independent pre-PR review, cycle 1, both lenses).
        const string file = $"{TemplateDirectory}/self-review-phase.md";
        string tipFile = Path.Combine(Path.GetTempPath(), $"self-review-round-one-tip-{Path.GetFileName(worktreePath)}")
            .Replace('\\', '/');
        AppendFragment(prompt, file, (project.VerifyCommands.Count == 0, recomposeFollows) switch
        {
            (true, true) => "opening-no-gates-recompose",
            (true, false) => "opening-no-gates-no-recompose",
            (false, true) => "opening-gates-recompose",
            (false, false) => "opening-gates-no-recompose",
        });
        AppendFragment(prompt, file, "hats-and-cap");
        if (stackedForkPointCommit is not null)
        {
            AppendFragment(prompt, file, "round-one-stacked",
                ("StackedForkPointCommit", stackedForkPointCommit), ("EffectiveBaseBranch", effectiveBaseBranch));
        }
        else
        {
            AppendFragment(prompt, file, "round-one-unstacked", ("EffectiveBaseBranch", effectiveBaseBranch));
        }

        AppendFragment(prompt, file, "tip-file-and-hunts", ("TipFile", tipFile));
        AppendFragment(prompt, file, (project.VerifyCommands.Count == 0, recomposeFollows) switch
        {
            (true, true) => "rerun-no-gates-recompose",
            (true, false) => "rerun-no-gates-no-recompose",
            (false, true) => "rerun-gates-recompose",
            (false, false) => "rerun-gates-no-recompose",
        });
        AppendFragment(prompt, file, "round-two-and-cap", ("TipFile", tipFile));
    }

    /// <summary>
    /// Checkpoint commits as crash protection, and the end-of-work recompose that turns them into
    /// the branch's real history (task: build sessions stop stranding finished work uncommitted).
    /// Only the headless path of <see cref="Build"/> uses this — a fresh session's own initial
    /// work — because a follow-up resumes an existing PR branch and lands fixes through the
    /// fixup/autosquash flow <c>AgentPromptBuilder.AppendCommitStyleRules</c> already teaches;
    /// that authored-history path is unchanged by this rule.
    /// <para>
    /// Origin: three no-commit strandings in one night (2026-08-29, tasks 430decdb, b6dfcbe5,
    /// d1c6902c out of roughly ten fresh build sessions), each a large session that finished its
    /// work and then ended with everything uncommitted. Every one was caught by
    /// <c>VerificationRunner</c>'s pre-gate check and recovered by retry with the worktree
    /// retained, so detection already works; committing only at the end is what left the whole
    /// session exposed to an abnormal ending (context exhaustion, an early exit after
    /// backgrounding a long test run) — exactly the moment that end-of-session step never runs.
    /// Checkpoint commits move the loss surface from "the whole session" to "the last increment".
    /// </para>
    /// <para>
    /// The recompose step is why a mixed reset is the mechanism rather than an interactive rebase
    /// or a squash: it changes which commits exist without moving the working tree, so the tree
    /// the recomposed commits describe is provably the exact tree that just passed the full
    /// suite. Nothing may happen between the reset and the commit-plan invocation for the same
    /// reason — a fix or a test run in that gap would make the recomposed commits describe a tree
    /// that was never actually the one verified.
    /// </para>
    /// <para>
    /// The reset target is the branch's fork point (<c>git merge-base origin/{baseBranch} HEAD</c>),
    /// never <c>origin/{baseBranch}</c> itself: that remote-tracking ref lives in the shared bare
    /// repo and moves whenever anything else touches it during this session (another worktree's
    /// fetch, a closeout branch cleanup), so resetting straight to its tip would recompose commits
    /// that revert whatever merged into the base after this branch was cut (conformance and
    /// adversarial review, cycle 1). The merge-base is stable regardless — against the project's
    /// own base branch. It is NOT stable against a stacked child's parent branch, which is why
    /// <paramref name="stackedForkPointCommit"/> exists; see its own doc.
    /// </para>
    /// <para>
    /// The recompose rewrites this branch's own history over a tip a prior run of the same task may
    /// already have pushed (the retry-after-a-failed-`gh pr create` shape <c>PullRequestOpener</c>
    /// pushes with <c>--force-with-lease</c> for), so a retried session's recompose can leave the
    /// worktree diverged from `origin/&lt;branch&gt;` — same content, no shared ancestry — even
    /// though nothing external touched the branch. <c>GitWorktreeManager.SyncToOriginBestEffortAsync</c>
    /// used to treat every diverged-with-a-clean-tree resume as a rewrite-on-origin and hard-reset to
    /// the remote tip, which destroyed exactly this recompose (independent pre-PR review, cycle 1,
    /// both lenses): it now checks whether origin's tip was ever the branch's own tip, per the
    /// branch ref's own reflog rather than this worktree's private HEAD reflog (independent pre-PR
    /// review, cycle 2 — the branch ref's reflog is what survives a worktree removed and re-added
    /// on a surviving local branch, since the new worktree's own HEAD reflog starts empty), and
    /// keeps the local tip when it was. The tree-identity check in step 3 below is the
    /// same reasoning applied one level down: the recompose itself must not silently drop a file the
    /// commit-plan step forgot to stage.
    /// </para>
    /// </summary>
    /// <param name="stackedForkPointCommit">
    /// This branch's fork point as a literal commit, for a stacked session only — see
    /// <see cref="StackedForkPoint"/> for why a merge-base against a parent branch is the wrong
    /// answer. Null (every ordinary run) keeps the merge-base wording exactly as it was.
    /// </param>
    public static void AppendCheckpointCommitRules(
        StringBuilder prompt, ProjectDetails project, string worktreePath, string? baseBranchOverride = null,
        string? stackedForkPointCommit = null)
    {
        const string file = $"{TemplateDirectory}/checkpoint-commit-rules.md";
        string baseBranch = baseBranchOverride ?? project.BaseBranch;
        AppendFragment(prompt, file, "commit-as-you-go");
        AppendSelfReviewPhaseRules(
            prompt, project, worktreePath, baseBranch: baseBranch,
            stackedForkPointCommit: stackedForkPointCommit);
        AppendFragment(prompt, file, "recompose-heading");
        if (project.VerifyCommands.Count == 0)
        {
            AppendFragment(prompt, file, "no-gates");
        }
        else
        {
            AppendFragment(prompt, file, "gates-heading");
            AppendGateLines(prompt, project);
        }

        AppendFragment(prompt, file, "step0");
        // A stacked session never computes its own fork point (independent pre-PR review,
        // cycle 1, adversarial lens): the platform recorded it at the cut, and no command this
        // session can run recovers it once the parent has been force-pushed.
        if (stackedForkPointCommit is not null)
        {
            AppendFragment(prompt, file, "step1-stacked",
                ("StackedForkPointCommit", stackedForkPointCommit), ("BaseBranch", baseBranch));
        }
        else
        {
            AppendFragment(prompt, file, "step1-unstacked", ("BaseBranch", baseBranch));
        }

        AppendFragment(prompt, file, "step2");
        AppendFragment(prompt, file, "step3");
        AppendPullRequestSummaryStep(prompt, project, asNumberedStep: true);
        AppendFragment(prompt, file, "between-steps");
        AppendFragment(prompt, file, "final-clean-tree-rule");
    }

    private static void AppendGateLines(StringBuilder prompt, ProjectDetails project)
    {
        const string file = $"{TemplateDirectory}/gate-line.md";
        foreach (VerifyCommand gate in project.VerifyCommands)
        {
            AppendFragment(prompt, file, "line", ("Command", gate.Command));
        }
    }

    /// <summary>
    /// The step that makes the pull request the session's own work rather than the daemon's
    /// (Decisions Log #163). It runs last, after the tree-identity check, because
    /// it composes from the commits the recompose just made: a summary written before that step
    /// would describe checkpoints nobody will ever see.
    /// <para>
    /// It is safe to run there, after a check the whole protocol exists to protect, precisely
    /// because it writes nothing: no file, no commit, no index change. The prompt says so
    /// explicitly rather than leaving a session to wonder whether composing text has just
    /// invalidated the identity it verified one step earlier.
    /// </para>
    /// <para>
    /// Skill order is stated rather than left to discovery, and it is the repo's rule first
    /// (Brian's addition to the design, 2026-09-09): a repository that ships its own
    /// PR-description rule has a house voice, and the platform's own skill exists to be the
    /// fallback for one that does not. arx-platform's
    /// <c>.claude/commands/git/pr-description.md</c> is the known case, which is why the prompt
    /// names that path outright instead of describing the shape and hoping.
    /// </para>
    /// <para>
    /// Headless only, like every other rule inside <see cref="AppendCheckpointCommitRules"/>: the
    /// parser this text promises reads a headless session's own stream-json result payload, which
    /// an attended interactive session never produces. An operator's own pull request text arrives
    /// through <c>h9k task deliver</c> instead.
    /// </para>
    /// </summary>
    /// <param name="asNumberedStep">
    /// True inside a numbered recompose protocol, where this is step 4. False on
    /// <see cref="AppendDelegatedContractorCommitRules"/>'s unreadable-base-commit path, which has
    /// no numbered steps to be the fourth of: the step still applies there (that session's final
    /// message is captured exactly the same way), so it is worded as a rule of its own rather than
    /// dropped for want of a number.
    /// </param>
    private static void AppendPullRequestSummaryStep(
        StringBuilder prompt, ProjectDetails project, bool asNumberedStep)
    {
        const string file = $"{TemplateDirectory}/pull-request-summary-step.md";
        string indent = asNumberedStep ? "     " : "  ";
        AppendFragment(prompt, file, asNumberedStep ? "step-numbered" : "step-bulleted");
        AppendFragment(prompt, file, "made-line", ("Indent", indent));
        AppendFragment(prompt, 
            file, asNumberedStep ? "worktree-numbered" : "worktree-bulleted", ("Indent", indent));
        AppendFragment(prompt, file, "whose-voice", ("Indent", indent));
        if (project.HomeDirectory.HasValue)
        {
            AppendFragment(prompt, file, "installs-at-home", ("Indent", indent), ("SkillPath",
                Path.Combine(ProjectHomePaths.SkillsDirectory(project.HomeDirectory.Value), "pr-summary", "SKILL.md")));
        }
        else
        {
            AppendFragment(prompt, file, "installs-at-default", ("Indent", indent));
        }
        AppendFragment(prompt, file, "where-it-goes",
            ("Indent", indent), ("PrSummaryMarker", PrSummaryParser.Marker),
            ("HandoffMarker", HandoffParser.Marker), ("PrSummaryTitlePrefix", PrSummaryParser.TitlePrefix));
        AppendFragment(prompt, file, "what-to-leave-out", ("Indent", indent));
        AppendWritingConventions(
            prompt, indent, project.WritingConventions, PromptTemplates.Load(file, "writing-conventions-lead-in"));
        AppendFragment(prompt, file, "do-not-run-gh", ("Indent", indent));
    }

    /// <summary>
    /// The house conventions any prose an agent writes for people has to obey, carried verbatim
    /// out of the project's own <see cref="WritingConventions"/> setting rather than written here
    /// — which is what task 412afe6c made of the hard-coded copy this used to be. Every prompt in
    /// this codebase that asks a session to compose text a person will read under the owner's
    /// login calls this, so the conventions are stated in one voice wherever they appear.
    /// <para>
    /// Public because <c>Hall9k.Daemon.Execution.AgentPromptBuilder</c> and
    /// <see cref="ReviewLapPromptBuilder"/> both need the same words: the review-feedback lap's
    /// summary comment and thread replies, and the note a review lap drafts for the reviewer's own
    /// <c>h9k pr approve</c>, are posted to GitHub exactly as a pull request body is.
    /// </para>
    /// </summary>
    /// <param name="lead">
    /// The bullet's own sentence, naming which prose these conventions govern here. Per caller,
    /// because "it" means the pull request body in one prompt and a review comment in the next,
    /// and a bullet that named neither would leave the session to guess how far the rule reaches.
    /// </param>
    public static void AppendWritingConventions(
        StringBuilder prompt, string indent, WritingConventions conventions, string lead)
    {
        prompt.AppendLine($"{indent}- {lead}");
        prompt.Append(conventions.ToPromptLines($"{indent}  > "));
    }

    /// <summary>
    /// The delegated-contractor counterpart of <see cref="AppendCheckpointCommitRules"/>
    /// (adversarial review, cycle 1, TaskDelegateCommand.cs:367). <c>h9k task delegate</c>'s own
    /// contractor reuses an interactive claim's own worktree exactly as it stands, so unlike a
    /// fresh <c>h9k task start</c> build, it can inherit real commits the operator
    /// themselves authored before this delegation — those are not this contractor's history to
    /// rewrite, whatever they contain, the same "respect what is already here by default" the
    /// delegated-contractor framing above already states. <paramref name="delegationBaseCommit"/>
    /// is the exact commit this branch held the moment the operator dispatched this contractor
    /// (<c>TaskDelegateCommand.PrepareAsync</c>'s own <c>git rev-parse HEAD</c>, read before launch
    /// and embedded here literally): resetting to it, rather than to the branch's fork point
    /// against <c>origin/{baseBranch}</c> the way a fresh build does, recomposes only the
    /// checkpoints this contractor adds from here on, never anything that predates it.
    /// <para>
    /// When that commit could not be read (<paramref name="delegationBaseCommit"/> is null — git
    /// itself was unreadable, the same rare failure <c>InteractiveWorktreeGit</c>'s other callers
    /// already fold into "assume work exists" rather than a guess), there is no boundary left that
    /// is safe to reset to: guessing one risks rewriting exactly the inherited history this rule
    /// exists to protect. This case skips only the reset/recompose step — the self-review phase and
    /// the project's own verification gates still apply, since neither depends on being able to
    /// name that boundary, and dropping them silently handed back ungated, unreviewed work
    /// (conformance and adversarial review, cycle 1) — and the contractor's own checkpoint commits
    /// stand as its history unrecomposed.
    /// </para>
    /// </summary>
    private static void AppendDelegatedContractorCommitRules(
        StringBuilder prompt, ProjectDetails project, string worktreePath, string? delegationBaseCommit,
        string baseBranch, string? stackedForkPointCommit)
    {
        const string file = $"{TemplateDirectory}/delegated-contractor-commit-rules.md";
        AppendFragment(prompt, $"{TemplateDirectory}/checkpoint-commit-rules.md", "commit-as-you-go");
        AppendSelfReviewPhaseRules(
            prompt, project, worktreePath, recomposeFollows: delegationBaseCommit is not null,
            baseBranch: baseBranch, stackedForkPointCommit: stackedForkPointCommit);

        if (delegationBaseCommit is null)
        {
            if (project.VerifyCommands.Count > 0)
            {
                AppendFragment(prompt, file, "no-base-suite-heading");
                AppendGateLines(prompt, project);
            }

            AppendFragment(prompt, file, "no-base-reset-rule");
            AppendPullRequestSummaryStep(prompt, project, asNumberedStep: false);
            AppendFragment(prompt, file, "no-base-final-clean-tree-rule");
            return;
        }

        AppendFragment(prompt, file, "recompose-heading");
        AppendFragment(prompt, file, project.VerifyCommands.Count == 0 ? "no-gates" : "gates-heading");
        if (project.VerifyCommands.Count > 0)
        {
            AppendGateLines(prompt, project);
        }

        AppendFragment(prompt, file, "step0");
        AppendFragment(prompt, file, "step1",
            ("DelegationBaseCommit", delegationBaseCommit), ("BaseBranch", baseBranch));
        AppendFragment(prompt, file, "step2");
        AppendFragment(prompt, file, "step3");
        AppendPullRequestSummaryStep(prompt, project, asNumberedStep: true);
        AppendFragment(prompt, file, "between-steps");
        AppendFragment(prompt, file, "final-clean-tree-rule");
    }

    /// <summary>
    /// The doctrine backlog 57 exists to teach: a dispatched session's process is killed the
    /// instant its final message ends, so nothing scheduled to happen after that moment — a
    /// backgrounded command, a scheduled wakeup, a monitor waiting to report back — ever runs.
    /// The interactive tools that assume otherwise (background execution, wakeup scheduling,
    /// monitors) are available in a dispatched session exactly as they are in an interactive
    /// one, and nothing about their own descriptions says they are inert here, so the prompt has
    /// to say so plainly rather than leaving it to be discovered by the run that hangs.
    /// <para>
    /// Origin evidence, all 2026-08-26: task df277369 failed twice in a row, both sessions
    /// backgrounding the test suite and ending the session waiting for a notification that
    /// would never come (the second attempt used ScheduleWakeup and Monitor explicitly); the PR
    /// #53 follow-up's cycle-3 fix round left eight files uncommitted, caught only by the next
    /// review pass; four-plus prior fix sessions logged an "(undeclared)" outcome under the same
    /// backlog item. <c>VerificationRunner</c>'s pre-gate check is the other half of this fix —
    /// it fails a run honestly when uncommitted work is left behind — but the failure is cheaper
    /// to prevent than to diagnose after the fact, which is what this prompt rule is for.
    /// </para>
    /// <para>
    /// Headless only (<see cref="Build"/>'s <c>isInteractive</c> flag routes to
    /// <see cref="AppendCommitDisciplineRuleForInteractiveSession"/> instead): an operator's
    /// attached session is not killed at its final message, so telling it otherwise would be a
    /// false claim about its own runtime, and would wrongly talk it out of backgrounding a long
    /// gate while it waits (adversarial review, cycle 4).
    /// </para>
    /// <para>
    /// The foreground-gates half of this rule (task: a headless build, fix, or recovery session
    /// never ends its turn while a gate it started is still running in the background) is factored
    /// into <see cref="AppendForegroundGatesRule"/> so the same wording reaches the two headless
    /// legs that never called this method at all — <c>BuildReviewVerify</c> (read-only, so the
    /// commit-discipline half below does not apply to it) and <c>BuildUncommittedWorkRecovery</c>
    /// (already carries its own narrower commit instructions). Origin: three headless sessions in
    /// eleven hours (2026-09-07/08, all claude-sonnet-5) each ended a turn with a `dotnet test` run
    /// still backgrounded — one via <c>run_in_background</c>, one merely narrating that a suite was
    /// "still running", one having set a <c>Monitor</c> — even though this method's own prior
    /// wording already said "run every verification command... in the foreground". Naming the
    /// harness's own background tools by name, saying explicitly that ending the turn with one
    /// still pending is the failure (not only "not relying on its result"), and telling the
    /// session to request an explicit per-command `timeout` up to the actual foreground ceiling
    /// (<c>BASH_MAX_TIMEOUT_MS</c>) rather than accepting the lower default a command gets with
    /// none, so a session can see the whole suite fits without ever reaching for a background
    /// tool, is what this task adds on top of the existing wording.
    /// </para>
    /// </summary>
    public static void AppendSessionEndsAtFinalMessageRule(StringBuilder prompt, TimeSpan commandTimeout)
    {
        const string file = $"{TemplateDirectory}/session-ends-at-final-message.md";
        AppendFragment(prompt, file, "head");
        AppendForegroundGatesRule(prompt, commandTimeout);
        AppendFragment(prompt, file, "tail");
    }

    /// <summary>
    /// The one rule every headless leg's prompt states, once, about how a build or test gate is
    /// run (task: a headless build, fix, or recovery session never ends its turn while a gate it
    /// started is still running in the background). Called from
    /// <see cref="AppendSessionEndsAtFinalMessageRule"/> for the legs that already carry that
    /// rule's full commit-discipline wording, and directly from
    /// <c>Hall9k.Daemon.Execution.AgentPromptBuilder.BuildReviewVerify</c> (the read-only verify
    /// leg, which never reaches the commit half) and
    /// <c>Hall9k.Daemon.Execution.AgentPromptBuilder.BuildUncommittedWorkRecovery</c> (the commit
    /// recovery leg, which already states its own narrower commit instructions) — the two legs
    /// AGENTS.md's Decisions Log entry for this task names as never having called
    /// <see cref="AppendSessionEndsAtFinalMessageRule"/> at all.
    /// <para>
    /// Names the harness's background tools explicitly — Bash's own <c>run_in_background</c>,
    /// <c>Monitor</c>, <c>ScheduleWakeup</c> — because the origin incidents show a generic
    /// "run gates in the foreground" sentence was not enough to stop a session reaching for one of
    /// these by name once a suite ran long: two of the three sessions this task's origin cites had
    /// already read that sentence and backgrounded the suite anyway, one of them via
    /// <c>ScheduleWakeup</c> and <c>Monitor</c> specifically. States both halves of the ceiling
    /// rather than only the higher one: a command run with no explicit `timeout` gets
    /// <c>BASH_DEFAULT_TIMEOUT_MS</c> (<paramref name="commandTimeout"/> itself, the same value
    /// <see cref="ClaudeSettingsFile.Build"/> sizes it to), and only an explicit per-command
    /// `timeout` reaches <c>BASH_MAX_TIMEOUT_MS</c> (double that) — so a session sees that the
    /// project's whole suite fits inside one foreground command only if it asks for the higher
    /// ceiling, rather than reading a bare mention of the ceiling as something it gets by
    /// default and discovering otherwise mid-suite. <paramref name="commandTimeout"/> is the live
    /// ceiling the caller's own session actually launches with — <c>ClaudeExecutor</c> sizes it
    /// from <c>DaemonOptions.VerifyGateTimeout</c> rather than a compile-time constant, so this
    /// rule reads the same value rather than a number that goes stale the moment an operator
    /// raises the live option (independent pre-PR review, cycle 1, both lenses; the same staleness
    /// <c>ClaudeSettingsFile</c>'s own 2026-09-02 finding recorded for <see cref="ClaudeSettingsFile.Build"/>).
    /// <paramref name="sessionRunsGates"/> is false for the two legs that must never run a gate at
    /// all — <c>BuildReviewVerify</c>, forbidden from writing into the worktree, and
    /// <c>BuildUncommittedWorkRecovery</c>, told not to run or wait on anything — so their rendered
    /// prompt does not open with an imperative to run the very thing the surrounding rules forbid
    /// (independent pre-PR review, cycle 1, both lenses: the unconditional wording once told a
    /// read-only reviewer to build and test in a worktree a sibling pass was reading, and told a
    /// commit-only recovery session to run a suite it had just been told not to wait on).
    /// </para>
    /// </summary>
    public static void AppendForegroundGatesRule(
        StringBuilder prompt, TimeSpan commandTimeout, bool sessionRunsGates = true)
    {
        // defaultCeilingMinutes rounds UP (cycle 4, adversarial lens, this method's own prior
        // finding): ClaudeSettingsFile.Build sizes BASH_DEFAULT_TIMEOUT_MS straight from the
        // millisecond value, so truncating here would understate what a bare command actually
        // gets. foregroundCeilingMinutes is computed independently from the real TimeSpan value —
        // never by doubling the already-rounded-up default — and rounds DOWN (independent pre-PR
        // review, this task's own ride-along finding): doubling a ceiling'd default overstated
        // BASH_MAX_TIMEOUT_MS for any non-integral-minute commandTimeout (9.5 minutes stated a
        // 20-minute cap while ClaudeSettingsFile.Build's own exact doubling of the millisecond
        // value enforces 19), and overstating the one number a session actually requests as an
        // explicit `timeout` is the dangerous direction: the command is clamped short and dies
        // mid-suite, the precise failure mode this whole rule exists to prevent.
        int defaultCeilingMinutes = (int)Math.Ceiling(commandTimeout.TotalMinutes);
        int foregroundCeilingMinutes = ForegroundCeilingMinutes(commandTimeout);
        const string file = $"{TemplateDirectory}/foreground-gates.md";
        AppendFragment(prompt, 
            file, sessionRunsGates ? "session-runs-gates" : "session-does-not-run-gates",
            ("DefaultCeilingMinutes", defaultCeilingMinutes.ToString(CultureInfo.InvariantCulture)),
            ("ForegroundCeilingMinutes", foregroundCeilingMinutes.ToString(CultureInfo.InvariantCulture)));

        AppendNoHostLoadForFlakeReproductionRule(prompt, "  ", sessionRunsGates);
    }

    /// <summary>
    /// AGENTS.md's own bullet beside the foreground-gates rule (Decisions Log
    /// §16 #169): a dispatched session never generates host load to reproduce or
    /// prove a flaky or timing-dependent test. Folded into <see cref="AppendForegroundGatesRule"/>
    /// itself, both branches, so it reaches every leg that already carries that rule without a
    /// separate call site to keep in sync — and called directly from the two bare <c>--resume</c>
    /// retry legs (<c>AgentPromptBuilder.BuildBudgetRetry</c>, <c>BuildSessionErrorRetry</c>),
    /// which restate no other doctrine at all and so never reach
    /// <see cref="AppendForegroundGatesRule"/> any other way.
    /// <para>
    /// Origin: 2026-09-10 09:36 EDT on the Windows node, task 2c6e95f7 (the two
    /// <c>VerificationRunnerTests</c> gate-retry tests that had flaked the night before), run
    /// 01a08b83 — the build session spawned forty pwsh stress loops (4.4 GB, CPU pinned) to try to
    /// reproduce the flake under load; h9kd and its own Postgres connections were starved for
    /// seven minutes, the run was orphaned, and Brian rebooted the machine. Brian's ruling the same
    /// day: "We should never stress the host just to chase down a stupid test" — some flaky tests
    /// will happen, and the right response fixes them as well as possible without proving the fix
    /// at the expense of other logic, host processes, or memory; the ruling does not forbid running
    /// this project's own gates, only load whose purpose is to chase or prove a flake. Same family
    /// as Decisions Log #108, #132, and #157 (the Postgres container cap exists because unbounded
    /// parallelism OOM'd the Mac; a faster test suite does less work, never more at once).
    /// </para>
    /// <para>
    /// <paramref name="indent"/> lets a caller already inside a bulleted continuation block (every
    /// <see cref="AppendForegroundGatesRule"/> caller) match its own two-space indent, while the two
    /// bare-prose retry legs call this with no indent at all.
    /// </para>
    /// <para>
    /// <paramref name="sessionRunsGates"/> mirrors <see cref="AppendForegroundGatesRule"/>'s own
    /// parameter (independent pre-PR review, this task's own ride-along finding): the forbidden
    /// shapes are stated either way — even a read-only reviewer or a commit-only recovery session
    /// must never reach for one — but the permitted deterministic-reproduction path only makes
    /// sense for a session actually told to run this project's own gates. An earlier draft stated
    /// "then run the suite once, in the foreground, the same as any other gate" unconditionally,
    /// which directly followed — and contradicted — the read-only/commit-only branch's own "never
    /// start anything" wording for <see cref="AgentPromptBuilder.BuildReviewVerify"/> and
    /// <see cref="AgentPromptBuilder.BuildUncommittedWorkRecovery"/>.
    /// </para>
    /// </summary>
    public static void AppendNoHostLoadForFlakeReproductionRule(
        StringBuilder prompt, string indent = "", bool sessionRunsGates = true)
    {
        const string file = $"{TemplateDirectory}/no-host-load.md";
        AppendFragment(prompt, file, "head", ("Indent", indent));
        AppendFragment(prompt, 
            file, sessionRunsGates ? "session-runs-gates" : "session-does-not-run-gates", ("Indent", indent));
    }

    /// <summary>
    /// The actual foreground ceiling (<c>BASH_MAX_TIMEOUT_MS</c>, in minutes) a session launched
    /// with <paramref name="commandTimeout"/> as its <c>BASH_DEFAULT_TIMEOUT_MS</c> gets, per
    /// <see cref="ClaudeSettingsFile.Build"/>'s exact doubling of the millisecond value. Rounds
    /// DOWN rather than doubling an already-rounded-up default, for the reason
    /// <see cref="AppendForegroundGatesRule"/>'s own inline comment records: doubling a ceiling'd
    /// default overstates the one number a session actually requests as an explicit `timeout`,
    /// which is the dangerous direction — the command is clamped short and dies mid-suite.
    /// Public so every rendered rule naming this ceiling — the rule itself, and the review-fix
    /// self-check phase's own foreground-test instruction — reads the same computed value rather
    /// than a second number that can drift out of step with it (independent pre-PR review, cycle
    /// 3, conformance lens: the self-check phase previously stated a separate, stale 590-600
    /// second figure instead of the ceiling this method already computes).
    /// </summary>
    public static int ForegroundCeilingMinutes(TimeSpan commandTimeout) =>
        (int)Math.Floor(commandTimeout.TotalMinutes * 2);

    /// <summary>
    /// The interactive counterpart of <see cref="AppendSessionEndsAtFinalMessageRule"/>: an
    /// operator's own attached session keeps running background commands, scheduled wakeups, and
    /// monitors exactly as any other interactive session does, so this drops the false
    /// process-dies-at-final-message claim rather than repeating it (adversarial review, cycle
    /// 4). The commit-everything discipline still applies, for a different reason —
    /// <c>h9k task verify</c> and <c>h9k task deliver</c> read this worktree, and
    /// <c>h9k task deliver</c> refuses to push outright over a modified-but-uncommitted file or a
    /// new, never-<c>git add</c>ed one under src/ or tests/ (commit <c>3e582806</c> widened the
    /// refusal to the latter; a rule still claiming only modified files block delivery would be
    /// stale again — independent pre-PR review, cycle 3).
    /// </summary>
    public static void AppendCommitDisciplineRuleForInteractiveSession(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/interactive-commit-discipline.md";
        AppendFragment(prompt, file, "rule", ("Deliver", DeliverWord));
    }

    /// <summary>
    /// The escape hatch <c>Hall9k.Cli.Commands.InteractiveSessionLiveness.IsSelfInvocation</c>
    /// opens (this project cannot reference the CLI, so it is named rather than linked): this very
    /// session, not only the operator, may run <c>h9k task deliver</c>, <c>h9k task handback</c>, or
    /// <c>h9k task release</c> against its own claim without the double-booking guard refusing it as
    /// "still attached elsewhere" (Decisions Log #126 — "the prompt-handoff model's whole point is a
    /// session that keeps running past the build and delivers itself on the operator's own go").
    /// Two things follow from that which the guard itself cannot enforce, so the prompt states them
    /// instead (independent pre-PR review, cycle 1, both lenses).
    /// <para>
    /// First: <c>h9k task deliver</c>'s own operator-facing handoff prompt
    /// (<c>TaskDeliverCommand.PromptForHandoff</c>) blocks on an interactive terminal that a Bash
    /// tool call structurally does not have — stdin and stdout are both redirected there — so a
    /// self-delivery that omits <c>--handoff</c> silently writes a blank one, and a dependent task
    /// starts from nothing. Passing <c>--handoff "&lt;text&gt;"</c> explicitly is the only way this
    /// session's own handoff ever reaches a dependent.
    /// </para>
    /// <para>
    /// Second: succeeding at any of the three commands hands this worktree to whatever comes next —
    /// the platform's own gates and review sessions once delivered, a fresh headless run once handed
    /// back — immediately, not once this session itself ends. Continuing to edit or run tests here
    /// afterward races whichever process just took it over.
    /// </para>
    /// </summary>
    public static void AppendSelfDeliveryRule(StringBuilder prompt)
    {
        const string file = $"{TemplateDirectory}/self-delivery.md";
        AppendFragment(prompt, file, "rule", ("Deliver", DeliverWord));
    }

    /// <summary>
    /// The prompt-handoff model's own connector rule (R4, idea fcaded0b's design rulings, Take the
    /// Wheel epic 9272e514's slice 7): <c>h9k task work</c> no longer launches this session and
    /// records its pid itself the way a direct launch's own <c>onStarted</c> callback did — it
    /// printed this prompt for the operator to paste into a Claude Code session they started on
    /// their own, so hall9k has not observed this session exists yet. Naming the observation gate
    /// directly (<c>Hall9k.Cli.Commands.TaskRegisterSessionCommand</c> — this project cannot
    /// reference the CLI, so it is named rather than linked) and what happens without it, the same
    /// concreteness <see cref="AppendExternalInteractionLoggingRule"/> already gives the escape-hatch
    /// invariant, rather than a vague "please identify yourself" a session could reasonably read as
    /// optional.
    /// </summary>
    public static void AppendSelfRegistrationRule(StringBuilder prompt, Guid taskId)
    {
        const string file = $"{TemplateDirectory}/self-registration.md";
        AppendFragment(prompt, 
            file, "rule", ("RegisterSession", RegisterSessionWord), ("TaskId", taskId.ToString()),
            ("Deliver", DeliverWord));
    }

    /// <summary>
    /// R4 and R8 together: a session started this way still needs to find and message whatever
    /// other agents this task already has running (a re-entry into a claim a headless follow-up
    /// once touched, or simply a curious operator asking what else is live). <see cref="SessionRoleName"/>
    /// is the fixed vocabulary every dispatched session's own name is drawn from; <c>h9k task show</c>
    /// is the query surface that already renders it (<c>TaskShowCommand.WriteSessionsAsync</c>'s own
    /// doc has the mechanism) — named here rather than assumed known, the same reasoning
    /// <see cref="AppendProjectHome"/> already applies to the project's own paths.
    /// </summary>
    public static void AppendFindLiveAgentsRule(StringBuilder prompt, Guid taskId)
    {
        const string file = $"{TemplateDirectory}/find-live-agents.md";
        AppendFragment(prompt, file, "rule", ("TaskId", taskId.ToString()));
    }

    /// <summary>
    /// Restates, rather than only recommends, the two platform-imposed settings
    /// <c>Hall9k.Connectors.Prompts.ClaudeSettingsFile.Build</c> normally guarantees through
    /// <c>--settings</c> — a direct launch always passes that flag itself, but a session started
    /// this way was pasted into wherever the operator happened to start it, and
    /// <c>h9k task work</c> only ever recommends the flag on the printed handoff rather than
    /// enforcing it, so a session launched without it would otherwise never learn either rule from
    /// anywhere (independent pre-PR review, adversarial lens, cycle 1). Read this rule as a
    /// fallback for the case <c>--settings</c> was skipped, not as a replacement for recommending
    /// it — the printed handoff still names the flag, and a session that does carry it is simply
    /// told nothing new here.
    /// </summary>
    public static void AppendPlatformSettingsReminderRule(StringBuilder prompt, ProjectDetails project)
    {
        const string file = $"{TemplateDirectory}/platform-settings-reminder.md";
        AppendFragment(prompt, file, "lead");
        if (project.VerifyCommands.Count == 0)
        {
            AppendFragment(prompt, file, "no-gates");
        }
        else
        {
            AppendFragment(prompt, file, "with-gates", ("Gates", string.Join(", ",
                project.VerifyCommands.Select(gate => $"`{gate.Command}`"))));
        }
    }

    /// <summary>
    /// The escape-hatch invariant (the 2026-09-01 ruling, idea fcaded0b's design rulings 4 and 5):
    /// any interaction this session has with a party outside it — another agent session reached
    /// through the mesh, a human steering it that way, an external API this task's own prompt did
    /// not already route through one of the platform's other observation-gate commands — is
    /// logged through the platform unconditionally, even one the interacting party asks the
    /// session to keep quiet. <c>h9k task log-interaction</c> is the CLI surface it lands through
    /// (an agent-facing command, structured the same way <c>h9k task write-jira</c> is — but,
    /// unlike that one, not itself an observation gate: there is nothing external here to verify
    /// the claim against): what reaches the run stream is structured, not left to transcript
    /// prose, and a human-directed
    /// entry rides forward into a later review pass through the same settled-rulings surface a
    /// human's own <c>h9k review resolve</c> verdict already does (Decisions Log #88). Handed the
    /// task's own id directly (<paramref name="taskId"/>) rather than left for the agent to look
    /// up, the same way every other agent-facing command example in these prompts already embeds
    /// one.
    /// <para>
    /// This is best-effort by construction, not an enforcement mechanism, and the rule says so
    /// rather than overclaiming: nothing here can force a call the session declines to make, and
    /// the platform only ever records what its own channels — this command, a gate, an observed
    /// git or GitHub state — can actually see.
    /// </para>
    /// </summary>
    public static void AppendExternalInteractionLoggingRule(StringBuilder prompt, Guid taskId)
    {
        const string file = $"{TemplateDirectory}/external-interaction-logging.md";
        AppendFragment(prompt, file, "rule", ("TaskId", taskId.ToString()));
    }

    /// <summary>
    /// R8's outbound half of interactive mode (task: agents on an interactive-mode task report
    /// outbound, idea fcaded0b's design rulings): a small, fixed vocabulary of milestones
    /// (<see cref="Hall9k.Domain.Features.Run.OutboundMilestone"/>) this dispatched agent sends to
    /// the human's own registered session — not a running commentary, and not a substitute for
    /// slice 8's own park, which holds at the phase boundary regardless of whether any message
    /// ever lands. This method opens its own "##" heading, so every caller places it AFTER the
    /// rest of its own bullet list — never in the middle of one — so the heading closes that list
    /// out instead of nesting whatever came after it underneath "Reporting to the human"
    /// (independent pre-PR review, cycle 1, both lenses: the two callers that continue a bullet
    /// list past this call had exactly that bug). A milestone send is exactly the outside-
    /// interaction case <see cref="AppendExternalInteractionLoggingRule"/> already commits this
    /// session to logging — called earlier in every caller's own prompt, not necessarily
    /// immediately above — restated here only for the parts that rule cannot know on its own —
    /// when to send, and who to address.
    /// <para>
    /// The build role's own address is null on every production path today (independent pre-PR
    /// review, cycle 1, adversarial lens): a fresh headless build dispatch under interactive mode
    /// starts a brand-new <c>RunAggregate</c> stream, and nothing yet carries a registration
    /// forward from an earlier run of the same task, so its milestones always take the
    /// no-registered-session branch below and log a skip. <c>h9k task delegate</c>'s own contractor
    /// is the one build dispatch that does not start a fresh stream — it reuses the operator's
    /// existing run — but <c>TaskDelegateCommand</c> still never resolves that run's own
    /// registration before calling this, so its address is null here too, for a different reason
    /// (see <paramref name="isDelegatedContractor"/>). The review and fix roles, dispatched later on
    /// that same run once a human's own <c>h9k task work</c> claim registered against it, are the
    /// roles this can actually reach a real address for.
    /// </para>
    /// </summary>
    /// <param name="phaseLabel">Names the phase in the bound sentence ("build", "review", "fix") — cosmetic only.</param>
    /// <param name="milestones">
    /// This role's own fixed list (<see cref="Hall9k.Domain.Features.Run.OutboundMilestone"/>);
    /// its length IS the bound this method states, not a separately-tracked number.
    /// </param>
    /// <param name="address">
    /// The human's registered interactive session name for this run
    /// (<c>RunDetails.RegisteredInteractiveSessionName</c>), resolved by the caller at
    /// prompt-build time. Null when nobody has ever run <c>h9k task register-session</c> against
    /// this run — every fresh headless dispatch under interactive mode starts this way
    /// (<c>h9k task start</c>, an ordinary dispatch carrying the flag forward from an earlier
    /// <c>h9k task release --keep-interactive</c>, or a retry, reopen, or follow-up redispatch:
    /// each one starts a new <c>RunAggregate</c> stream, and no registration carries forward from
    /// an earlier one yet) — or, for <c>h9k task delegate</c>'s own contractor
    /// (<paramref name="isDelegatedContractor"/>), because <c>TaskDelegateCommand</c> never resolves
    /// the reused run's own registration at all, so a null address there is not the same observed
    /// fact it is everywhere else. A handback is never one of these cases: <c>TaskDecider.HandBack</c>
    /// clears the interactive-mode flag unconditionally, so a handback-dispatched run never calls
    /// this method at all. Blank (not null) when someone did register but their own session
    /// carries no display name to send to. Both skip sending, per AGENTS.md's own "never guess at
    /// unobserved facts" rule, rather than invent a name for either — but this method still tells
    /// the two apart in what it tells the agent: "nobody registered" is a different fact from
    /// "someone registered with nowhere to send to", and stating the wrong one of the two would
    /// itself be a guess (Copilot review on PR #236).
    /// </param>
    /// <param name="parksAtBoundaryAfterward">
    /// True (the default) when slice 8's own phase-boundary park actually holds the instant this
    /// session ends. No caller currently passes false: a build session dispatched by
    /// <c>h9k task start</c> (<c>isDeliberateHeadlessStart</c>) used to, on the premise that
    /// nothing supervised that run once it started, but <c>RunSupervisor.AdoptDeliberateHeadlessStartsAsync</c>
    /// now does (task: a do-now session launched by h9k task start is caught within seconds) —
    /// once it delivers a clean, committed tree automatically, the review loop's own first
    /// boundary parks for the human exactly as it would for any other interactive-mode build, so
    /// asserting otherwise contradicted the "## Working rules" section this same prompt already
    /// gives that session (independent pre-PR review, cycle 3, conformance lens). The parameter
    /// stays so a future launch shape genuinely unsupervised at this boundary can say so without a
    /// second copy of this method.
    /// </param>
    /// <param name="isDelegatedContractor">
    /// True only for <c>h9k task delegate</c>'s own contractor. Every other build dispatch this
    /// method's <paramref name="address"/> doc already enumerates starts a brand-new
    /// <c>RunAggregate</c> stream, so a null address there really does mean nobody has ever
    /// registered against it — but a delegated contractor reuses the operator's own existing run,
    /// which may have carried a real registration earlier in the same claim (independent pre-PR
    /// review, cycle 1, both lenses). <c>TaskDelegateCommand</c> never resolves that registration
    /// before calling this, so a null address here does not mean "never registered" the way it does
    /// everywhere else — this flag keeps the null-address branch from asserting that anyway.
    /// </param>
    /// <param name="verdictBoundaryChoicesTaskId">
    /// The task's own id, supplied only by the review role (task: a human at the wheel takes the
    /// fix role herself, fourth criterion), which makes the end-of-phase report below also name
    /// the choices the human actually has at the park this session's own verdict lands them at —
    /// with the exact command for each, from the one shared vocabulary
    /// (<see cref="InteractiveBoundaryChoices"/>). Null for the build and fix roles: each of those
    /// ends at a boundary whose only levers are the plain proceed-or-redirect pair the milestone
    /// sentence already names, so a whole block enumerating them would be noise. A review pass is
    /// the one role whose own verdict decides WHICH boundary comes next, which is exactly why it
    /// is the one that has to say.
    /// </param>
    public static void AppendOutboundMilestoneRules(
        StringBuilder prompt, string phaseLabel, IReadOnlyList<string> milestones, string? address,
        bool parksAtBoundaryAfterward = true, bool isDelegatedContractor = false,
        Guid? verdictBoundaryChoicesTaskId = null)
    {
        const string file = $"{TemplateDirectory}/outbound-milestones.md";
        prompt.AppendLine();
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "lead",
            ("Count", milestones.Count.ToString(CultureInfo.InvariantCulture)),
            ("MessageWord", milestones.Count == 1 ? "message" : "messages"),
            ("PhaseLabel", phaseLabel));
        prompt.AppendLine();
        for (int i = 0; i < milestones.Count; i++)
        {
            bool isFinal = i == milestones.Count - 1;
            AppendFragment(prompt, file, isFinal ? "milestone-final-head" : "milestone", ("Milestone", milestones[i]));
            if (isFinal)
            {
                AppendFragment(prompt, file, "milestone-final-body");
                bool sendsToAddress = address.IsNotBlank();
                if (parksAtBoundaryAfterward)
                {
                    AppendFragment(prompt, file,
                        sendsToAddress ? "parks-at-boundary-address-present" : "parks-at-boundary-no-address");
                }
                else
                {
                    AppendFragment(prompt, file,
                        sendsToAddress ? "does-not-park-address-present" : "does-not-park-no-address",
                        ("Deliver", DeliverWord));
                }
            }
        }

        prompt.AppendLine();
        string skipMilestonesFragment = parksAtBoundaryAfterward
            ? "skip-milestones-parks" : "skip-milestones-does-not-park";
        if (address.IsNotBlank())
        {
            AppendFragment(prompt, file, "address-present", ("Address", address));
        }
        else if (address is null && isDelegatedContractor)
        {
            AppendFragment(prompt, file, "no-address-delegated", ("RegisterSession", RegisterSessionWord));
            AppendFragment(prompt, file, skipMilestonesFragment, ("Deliver", DeliverWord));
            AppendFragment(prompt, file, "log-once");
        }
        else if (address is null)
        {
            AppendFragment(prompt, file, "no-address-ordinary", ("RegisterSession", RegisterSessionWord));
            AppendFragment(prompt, file, skipMilestonesFragment, ("Deliver", DeliverWord));
            AppendFragment(prompt, file, "log-once");
        }
        else
        {
            AppendFragment(prompt, file, "blank-address", ("RegisterSession", RegisterSessionWord));
            AppendFragment(prompt, file, skipMilestonesFragment, ("Deliver", DeliverWord));
            AppendFragment(prompt, file, "log-once");
        }

        if (verdictBoundaryChoicesTaskId is { } choicesTaskId)
        {
            AppendVerdictBoundaryChoices(prompt, choicesTaskId);
        }
    }

    /// <summary>
    /// What the review role's own end-of-phase report has to tell the human besides the verdict
    /// and findings themselves (task: a human at the wheel takes the fix role herself, fourth
    /// criterion): the choices the human actually has at the park this verdict lands them at, each
    /// with its exact command, so answering a boundary never means reading the docs first — and so
    /// a human agent reading this report can offer them in words.
    /// <para>
    /// Written as two conditionals rather than one list because a review pass does not know, at
    /// prompt-build time, which boundary its own verdict will produce — and a prompt that asserted
    /// one would be guessing. A needs-fixes verdict lands squarely at the review-verdict-to-fix
    /// boundary and its four choices. A merge-ready one is less determinate: the loop may still owe
    /// the mandatory final full pass (Decisions Log #92) and park at fix-to-re-review first, so
    /// that arm says out loud that one more review boundary can come first and names both — the
    /// fix-to-re-review pair, then the gates-to-pull-request choices for when the run actually
    /// settles. Every one of them rendered from
    /// <see cref="InteractiveBoundaryChoices.Lines"/>, never described in prose: a boundary
    /// summarised as "or an h9k review resolve redirect" is exactly the bare, un-copy-pasteable
    /// command that promise above forbids (Copilot review on PR #272).
    /// </para>
    /// </summary>
    private static void AppendVerdictBoundaryChoices(StringBuilder prompt, Guid taskId)
    {
        const string file = $"{TemplateDirectory}/verdict-boundary-choices.md";
        prompt.AppendLine();
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "lead");
        prompt.AppendLine();
        AppendFragment(prompt, file, "findings-need-fixes-intro", ("NeedsFixes", NeedsFixesWord));
        prompt.AppendLine();
        AppendBoundaryChoiceBullets(prompt, InteractiveBoundaryLevers.ReviewVerdictToFix, taskId);
        prompt.AppendLine();
        AppendFragment(prompt, file, "ready-to-merge-intro", ("MergeReady", MergeReadyWord));
        prompt.AppendLine();
        AppendBoundaryChoiceBullets(prompt, InteractiveBoundaryLevers.ProceedOrRedirect, taskId);
        prompt.AppendLine();
        AppendFragment(prompt, file, "settle-intro");
        prompt.AppendLine();
        AppendBoundaryChoiceBullets(prompt, InteractiveBoundaryLevers.GatesToPullRequest, taskId);
    }

    /// <summary>
    /// One bullet per choice, wrapped at the width the rest of these prompts use so a long command
    /// plus its explanation does not run off as a single line. Renders from
    /// <see cref="InteractiveBoundaryChoices.Lines"/> rather than restating the choices here: three
    /// surfaces each writing their own is how one of them ends up naming three where there are
    /// four.
    /// </summary>
    private static void AppendBoundaryChoiceBullets(
        StringBuilder prompt, InteractiveBoundaryLevers levers, Guid taskId)
    {
        foreach (string choice in InteractiveBoundaryChoices.Lines(levers, taskId))
        {
            prompt.AppendLine($"- {choice}");
        }
    }

    /// <summary>
    /// The same four-choice vocabulary, taught to the operator's OWN attached session at claim
    /// time (task: a human at the wheel takes the fix role herself, fourth criterion): the
    /// starting prompt <c>h9k task work</c> prints for the operator to paste into their own Claude
    /// Code session. They are the arbiter at every boundary this run reaches, and that session is
    /// what they will ask "what are my options here" — so it is told the same thing the review
    /// agents' own outbound reports will tell them, from the same source, rather than being left
    /// to read the docs or infer the commands from a park reason.
    /// <para>
    /// Only ever appended for an ATTACHED interactive claim on a task whose interactive-mode flag
    /// is on. A headless dispatch under the same flag has nobody at the terminal to offer choices
    /// to; the outbound-milestone rules are that path's own half of the same contract.
    /// </para>
    /// </summary>
    public static void AppendInteractiveBoundaryChoices(StringBuilder prompt, Guid taskId)
    {
        const string file = $"{TemplateDirectory}/interactive-boundary-choices.md";
        prompt.AppendLine();
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "lead");
        prompt.AppendLine();
        AppendFragment(prompt, file, "build-done-to-review");
        prompt.AppendLine();
        AppendBoundaryChoiceBullets(prompt, InteractiveBoundaryLevers.ProceedOrRedirect, taskId);
        prompt.AppendLine();
        AppendFragment(prompt, file, "review-verdict-to-fix-intro");
        prompt.AppendLine();
        AppendBoundaryChoiceBullets(prompt, InteractiveBoundaryLevers.ReviewVerdictToFix, taskId);
        prompt.AppendLine();
        AppendFragment(prompt, file, "gates-to-pull-request-intro", ("MergeReady", MergeReadyWord));
        prompt.AppendLine();
        AppendBoundaryChoiceBullets(prompt, InteractiveBoundaryLevers.GatesToPullRequest, taskId);
        prompt.AppendLine();
        AppendFragment(prompt, file, "tail");
    }

    /// <summary>
    /// Names the project's home and what is in it, so a dispatched session is told where
    /// everything lives instead of hunting for it. Silent for a project with no home, and for a
    /// home this node cannot see: an agent sent to a directory that is not there learns nothing
    /// and wastes a tool call finding out.
    /// </summary>
    public static void AppendProjectHome(StringBuilder prompt, ProjectDetails project)
    {
        if (!project.HomeDirectory.HasValue || !Directory.Exists(project.HomeDirectory.Value))
        {
            return;
        }

        const string file = $"{TemplateDirectory}/project-home.md";
        string home = project.HomeDirectory.Value;
        AppendFragment(prompt, file, "heading");
        prompt.AppendLine();
        AppendFragment(prompt, file, "home-line", ("Home", home));
        prompt.AppendLine();

        string agents = ProjectHomePaths.AgentsFile(home);
        if (File.Exists(agents))
        {
            AppendFragment(prompt, file, "agents-file-lines", ("AgentsFile", agents));
        }

        AppendFragment(prompt, file, "skills-line", ("SkillsDirectory", ProjectHomePaths.SkillsDirectory(home)));
        AppendFragment(prompt, file, "tasks-line", ("TasksDirectory", ProjectHomePaths.TasksDirectory(home)));
        AppendFragment(prompt, file, "ideas-line", ("IdeasDirectory", ProjectHomePaths.IdeasDirectory(home)));

        // Whether repo/ is actually populated is a filesystem fact, not a fact about RepositoryPath
        // alone (same test ProjectAgentsDocument.Render uses): `h9k project init --keep-repo-path`
        // materialises the bare clone and dev/ worktree without repointing the project at them, so
        // repo/ can be populated even while this session's own worktree — cut from wherever dispatch
        // actually reads project.RepositoryPath from — came from somewhere else.
        string bare = ProjectHomePaths.ResolveBareRepository(home, project.Name, project.RepositoryPath);
        string dev = ProjectHomePaths.DevWorktree(home);
        bool repoMaterialised = Directory.Exists(dev);
        bool dispatchesFromHome = ProjectHomePaths.SameDirectory(project.RepositoryPath, bare);
        string repoDirectory = ProjectHomePaths.RepoDirectory(home);
        if (dispatchesFromHome)
        {
            AppendFragment(prompt, file, "repo-dispatches-from-home", ("RepoDirectory", repoDirectory));
        }
        else
        {
            AppendFragment(prompt, file, repoMaterialised ? "repo-materialised-elsewhere" : "repo-empty-elsewhere",
                ("RepoDirectory", repoDirectory), ("RepositoryPath", project.RepositoryPath));
        }
        prompt.AppendLine();
        AppendFragment(prompt, file, "tail");
        prompt.AppendLine();
    }

    /// <summary>
    /// The home's own skills as a working rule, beside the repo's. Ordered from least specific to
    /// most: the install seeds the home, and the repo's <c>.claude/skills</c> is the tier for
    /// things genuinely coupled to the code — so a repo skill of the same name is the one that
    /// wins, and the home's copy of it is not listed twice.
    /// </summary>
    public static void AppendHomeSkillRule(
        StringBuilder prompt, ProjectDetails project, IReadOnlyList<RepoSkill> repoSkills)
    {
        IReadOnlyList<RepoSkill> homeSkills = [.. DiscoverHomeSkills(project)
            .Where(skill => !repoSkills.Any(repo => repo.Name == skill.Name))];
        if (homeSkills.Count == 0)
        {
            return;
        }

        string directory = ProjectHomePaths.SkillsDirectory(project.HomeDirectory.Value);
        AppendFragment(prompt, 
            $"{TemplateDirectory}/home-skill-rule.md", "lead", ("SkillsDirectory", directory));
        foreach (RepoSkill skill in homeSkills)
        {
            AppendSkillLine(prompt, skill);
        }
    }

    private static void AppendSkillLine(StringBuilder prompt, RepoSkill skill)
    {
        const string file = $"{TemplateDirectory}/skill-line.md";
        if (skill.Description is null)
        {
            AppendFragment(prompt, file, "without-description", ("Name", skill.Name));
        }
        else
        {
            AppendFragment(prompt, file, "with-description", ("Name", skill.Name), ("Description", skill.Description));
        }
    }

    public static IReadOnlyList<RepoSkill> DiscoverHomeSkills(ProjectDetails project) =>
        project.HomeDirectory.HasValue
            ? ReadSkills(ProjectHomePaths.SkillsDirectory(project.HomeDirectory.Value))
            : [];

    public static IReadOnlyList<RepoSkill> DiscoverRepoSkills(string worktreePath) =>
        ReadSkills(Path.Combine(worktreePath, ".claude", "skills"));

    /// <summary>
    /// One skills directory, read the same way wherever it sits: a subdirectory with a SKILL.md
    /// in it is a skill, and its frontmatter description is the one line the prompt carries.
    /// A symlinked skill directory is an ordinary one here — the seeding is symlinks by design,
    /// and Directory.EnumerateDirectories follows them.
    /// </summary>
    public static IReadOnlyList<RepoSkill> ReadSkills(string skillsDirectory)
    {
        if (!Directory.Exists(skillsDirectory))
        {
            return [];
        }

        List<RepoSkill> skills = [];
        foreach (string skillDirectory in Directory.EnumerateDirectories(skillsDirectory).Order(StringComparer.Ordinal))
        {
            string manifestPath = Path.Combine(skillDirectory, "SKILL.md");
            if (File.Exists(manifestPath))
            {
                skills.Add(new RepoSkill(Path.GetFileName(skillDirectory), ReadFrontmatterDescription(manifestPath)));
            }
        }

        return skills;
    }

    private static string? ReadFrontmatterDescription(string manifestPath)
    {
        // Stream and stop at the frontmatter fence — the skill body below it can be large
        // and is never needed here.
        using IEnumerator<string> lines = File.ReadLines(manifestPath).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.Trim() != "---")
        {
            return null;
        }

        while (lines.MoveNext())
        {
            string line = lines.Current;
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                string description = line["description:".Length..].Trim();
                return description.IsNotBlank() ? description : null;
            }
        }

        return null;
    }

    /// <summary>
    /// A named fragment out of a template file, substituted and appended through
    /// <see cref="PromptTemplates.AppendTemplate"/> rather than a bare <c>AppendLine</c> over the
    /// loaded text — a multi-line fragment's own internal line breaks otherwise never go through
    /// <see cref="StringBuilder.AppendLine()"/> at all, so they stay whatever this checkout's
    /// <c>.gitattributes</c> normalized them to (a bare <c>\n</c>) instead of
    /// <see cref="Environment.NewLine"/>, and a Windows-run session's rendered prompt ends up with
    /// mixed line endings (independent pre-PR review, cycle 1, both lenses). The <c>params</c>
    /// tuple array is this call site's whole parameter dictionary, spelled without one to build.
    /// </summary>
    private static void AppendFragment(
        StringBuilder prompt, string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.AppendTemplate(
            prompt, file, name, values.Length == 0 ? null : values.ToDictionary(value => value.Key, value => value.Value));
}

public sealed record RepoSkill(string Name, string? Description);
