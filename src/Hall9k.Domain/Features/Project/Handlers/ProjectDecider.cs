using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Handlers;

public static class ProjectDecider
{
    public static ProjectRegistered Register(
        Guid id,
        Guid ownerId,
        Guid connectionId,
        string name,
        string repositoryPath,
        Uri? repositoryUrl,
        string? baseBranch,
        DateTimeOffset registeredAt,
        ProjectHome? homeDirectory = null,
        bool skipPermissions = false)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("A project requires a name.");
        }

        if (repositoryPath.IsBlank())
        {
            throw new DomainValidationException("A project requires the local repository path the daemon creates worktrees from.");
        }

        RefuseRelativeRepositoryPath(repositoryPath);

        if (connectionId == Guid.Empty)
        {
            throw new DomainValidationException("A project binds to a connection, never to \"the machine's GitHub\" (PLAN.md §10).");
        }

        return new ProjectRegistered(
            id,
            ownerId,
            connectionId,
            name,
            repositoryPath,
            repositoryUrl,
            baseBranch.IsBlank() ? "main" : baseBranch,
            registeredAt,
            homeDirectory ?? ProjectHome.None,
            skipPermissions);
    }

    public static ProjectSettingsChanged ChangeSettings(
        ProjectAggregate project,
        Optional<IReadOnlyList<VerifyCommand>> verifyCommands,
        Optional<bool> skipPermissions,
        Optional<IReadOnlyList<ContextLink>> contextLinks,
        DateTimeOffset changedAt,
        Guid changedByOwnerId,
        Optional<CommitStyle> commitStyle = default,
        Optional<AgentModel> model = default,
        Optional<ReviewRerequestPolicy> reviewRerequest = default,
        Optional<JiraProjectKey> jiraProjectKey = default,
        Optional<ProjectHome> homeDirectory = default,
        Optional<string> repositoryPath = default,
        Optional<BacklogPolicy> backlogPolicy = default,
        Optional<string> backlogRoutingGuidance = default,
        Optional<int?> maxComplianceReviewCycles = default,
        Optional<int?> maxAdversarialReviewCycles = default,
        Optional<int?> maxFinalFullPassRounds = default,
        Optional<int?> lifetimeReviewCycleBudget = default,
        Optional<BranchNameTemplate> branchNameTemplate = default,
        Optional<string?> reviewStageComposition = default,
        bool reviewStageCompositionAcknowledged = false,
        Optional<AutoPrReviewSpeed> autoPrReview = default,
        bool acceptedBrokenGate = false,
        Optional<int?> maxParallelTasks = default,
        Optional<ProjectPriority> priority = default,
        Optional<ClaimGate> claimGate = default,
        Optional<IReadOnlyList<LaunchText>> launchTexts = default,
        Optional<AgentModel> orchestratorModel = default,
        Optional<CloseLinkedIssueRule> closeLinkedIssue = default,
        Optional<IReadOnlyList<string>> neverCloseLabels = default,
        Optional<WritingConventions> writingConventions = default)
    {
        if (repositoryPath.HasValue)
        {
            if (repositoryPath.Value.IsBlank())
            {
                throw new DomainValidationException(
                    "A project always has a local repository path the daemon creates worktrees from; "
                    + "there is no clearing it. Point it somewhere else instead.");
            }

            RefuseRelativeRepositoryPath(repositoryPath.Value);
        }

        // Zero is a legal value here, unlike every other cap in this decider: it is the
        // deliberate pause (Decisions Log #140), which is why the floor is 0 rather than 1.
        // Present-with-null clears the cap so the node ceiling alone decides again.
        if (maxParallelTasks is { HasValue: true, Value: { } tasks } && tasks < 0)
        {
            throw new DomainValidationException(
                $"MaxParallelTasks must be 0 or more, got {tasks}. It is a ceiling in task runs — how many "
                + "of this project's runs may be live at once (Decisions Log #140); 0 pauses the project, "
                + "and 'default' clears the cap so the node ceiling "
                + "(h9k config set --max-concurrent-task-runs) alone decides.");
        }

        // Unknown is a legal explicit value: it clears the project override so the
        // platform default applies again.
        if (commitStyle.HasValue
            && commitStyle.Value is { } style
            && style != CommitStyle.Unknown
            && style != CommitStyle.Narrative
            && style != CommitStyle.Append)
        {
            throw new DomainValidationException(
                $"CommitStyle must be {CommitStyle.Narrative} or {CommitStyle.Append} "
                + "(how follow-up runs land fixes on the PR branch, Decisions Log #26).");
        }

        // Unknown clears the project override, exactly as it does for CommitStyle. Anything
        // else must be spawnable: the value reaches the executor's shell command line, so a
        // model carrying shell metacharacters is rejected here rather than quoted and hoped for.
        if (model.HasValue && model.Value is { } chosen && chosen != AgentModel.Unknown && !chosen.IsWellFormed)
        {
            throw new DomainValidationException(
                $"'{chosen.Value}' is not a usable model name. Use a tier alias "
                + $"({AgentModel.Fable}, {AgentModel.Opus}, {AgentModel.Sonnet}, {AgentModel.Haiku}) or an exact "
                + $"model id (for example {AgentModel.PlatformFallback}); letters, digits, and . _ - : / @ [ ] only.");
        }

        // Unknown clears the orchestrator-window override, the identical clearing idiom `model`
        // itself uses just above.
        if (orchestratorModel.HasValue
            && orchestratorModel.Value is { } chosenOrchestratorModel
            && chosenOrchestratorModel != AgentModel.Unknown
            && !chosenOrchestratorModel.IsWellFormed)
        {
            throw new DomainValidationException(
                $"'{chosenOrchestratorModel.Value}' is not a usable model name. Use a tier alias "
                + $"({AgentModel.Fable}, {AgentModel.Opus}, {AgentModel.Sonnet}, {AgentModel.Haiku}) or an exact "
                + $"model id (for example {AgentModel.PlatformFallback}); letters, digits, and . _ - : / @ [ ] only.");
        }

        // Unknown clears the project override so the owner preference (or the node default)
        // decides again — the same clearing idiom CommitStyle and AgentModel use.
        if (reviewRerequest.HasValue
            && reviewRerequest.Value is { } policy
            && policy != ReviewRerequestPolicy.Unknown
            && policy != ReviewRerequestPolicy.Enabled
            && policy != ReviewRerequestPolicy.Disabled)
        {
            throw new DomainValidationException(
                $"The review re-request policy must be {ReviewRerequestPolicy.Enabled} or "
                + $"{ReviewRerequestPolicy.Disabled} (whether closeout asks the reviewers for another "
                + "pass after a fix follow-up pushes, Decisions Log #62).");
        }

        // Unknown is not a value here — None is (BacklogPolicy has no separate "no opinion"
        // level to defer to), so the closed set is exactly the three static instances, checked
        // the same way CommitStyle and AgentModel are: BacklogPolicy itself is only ever compared
        // against its own statics (== / !=), never interpolated anywhere, so a policy built some
        // way other than Parse or FromInput is refused here rather than trusted, on the same
        // discipline as its siblings above. What actually reaches an agent's prompt and, for
        // github-issues, a `gh` command line, is BacklogRoutingGuidance — deliberately left
        // unvalidated as free-text guidance, since it is the operator's routing instructions
        // rather than a closed set.
        if (backlogPolicy.HasValue
            && backlogPolicy.Value is { } chosenBacklogPolicy
            && chosenBacklogPolicy != BacklogPolicy.None
            && chosenBacklogPolicy != BacklogPolicy.GitHubIssues
            && chosenBacklogPolicy != BacklogPolicy.Jira)
        {
            throw new DomainValidationException(
                $"The backlog policy must be {BacklogPolicy.None}, {BacklogPolicy.GitHubIssues}, "
                + $"or {BacklogPolicy.Jira} (where a published task's work becomes visible outside Hall9k).");
        }

        ReviewCapValidation.RefuseNonPositiveCap(maxComplianceReviewCycles, "--max-compliance-review-cycles");
        ReviewCapValidation.RefuseNonPositiveCap(maxAdversarialReviewCycles, "--max-adversarial-review-cycles");
        ReviewCapValidation.RefuseNonPositiveCap(maxFinalFullPassRounds, "--max-final-full-pass-rounds");
        ReviewCapValidation.RefuseNonPositiveCap(lifetimeReviewCycleBudget, "--lifetime-review-cycle-budget");

        // Blank or "default" clears the project override so the node decides again — the same
        // clearing idiom every level-of-a-chain setting above already uses
        // (ReviewStageCompositionValidation.VetInput). Anything else must be one of the five
        // recognized compositions, and a value that removes a load-bearing guarantee must be
        // acknowledged.
        string? normalizedRaw = reviewStageComposition.HasValue
            ? ReviewStageCompositionValidation.VetInput(
                reviewStageComposition.Value, reviewStageCompositionAcknowledged, "--review-stage-composition")
            : null;

        // Rendered here, not merely shape-checked: a template is refused at the command line only
        // if the thing that will actually cut branches refuses it, so validation runs the same
        // BranchNameTemplate.Render the dispatcher will run, over representative tasks. The
        // alternative is a template accepted here and discovered at the dispatch it fails, which
        // is the failure this setting exists to stop rather than to relocate.
        if (branchNameTemplate.HasValue && branchNameTemplate.Value is { } chosenTemplate)
        {
            branchNameTemplate = BranchNameTemplate.Parse(chosenTemplate.Value);
        }

        // Normalized to the canonical value ("adversarial-only" round-trips as "AdversarialOnly") or
        // to null (blank/"default" clears), the same discipline BranchNameTemplate's own
        // Parse-before-recording gives its setting: what lands on the stream is what h9k project
        // show and the resolver will read back, not whatever alias or clearing word a human typed.
        Optional<ReviewStageComposition?> normalizedComposition = reviewStageComposition.HasValue
            ? Optional<ReviewStageComposition?>.Of(normalizedRaw is { } normalizedWord
                ? ReviewStageComposition.FromInput(normalizedWord)
                : null)
            : Optional<ReviewStageComposition?>.None;

        // Off is not a value to defer with here — the BacklogPolicy idiom, not the
        // CommitStyle/ReviewRerequestPolicy one: there is no owner- or node-level auto-pr-review
        // setting underneath this to fall back to, so the closed set is exactly the four static
        // instances, checked the same way BacklogPolicy is: only ever compared against its own
        // statics, never interpolated anywhere, so a speed built some way other than Parse or
        // FromInput is refused here rather than trusted.
        if (autoPrReview.HasValue
            && autoPrReview.Value is { } chosenSpeed
            && chosenSpeed != AutoPrReviewSpeed.Off
            && chosenSpeed != AutoPrReviewSpeed.Normal
            && chosenSpeed != AutoPrReviewSpeed.First
            && chosenSpeed != AutoPrReviewSpeed.Now)
        {
            throw new DomainValidationException(
                $"The auto-pr-review speed must be {AutoPrReviewSpeed.Off}, {AutoPrReviewSpeed.Normal}, "
                + $"{AutoPrReviewSpeed.First}, or {AutoPrReviewSpeed.Now} (how fast a GitHub reviewer "
                + "assignment to this install's own login starts the pr-review task it mints).");
        }

        // The same closed-set check, for the same reason (Decisions Log #141): a tier is only
        // ever compared against its own statics by the dispatcher's rotation, so an unrecognized
        // one would schedule as normal and silently do nothing — and a focus that quietly does
        // nothing is the one scheduling mistake an operator would not notice. Unknown is refused
        // along with everything else: it is what a value already on a stream reads as, never
        // something a caller may write.
        if (priority.HasValue
            && priority.Value is { } chosenTier
            && chosenTier != ProjectPriority.High
            && chosenTier != ProjectPriority.Normal
            && chosenTier != ProjectPriority.Low)
        {
            throw new DomainValidationException(
                $"The project priority must be {ProjectPriority.High}, {ProjectPriority.Normal}, or "
                + $"{ProjectPriority.Low} (which tier this project's ready work competes in for a free "
                + "dispatch slot, Decisions Log #141). A higher tier is focus and releases itself when "
                + "the project's queue drains; to stop a project entirely, pause it with "
                + "--max-parallel-tasks 0.");
        }

        // Off is not a value to defer with here either — the same BacklogPolicy/AutoPrReviewSpeed
        // idiom, and for the same reason: there is no owner- or node-level claim gate underneath
        // this project one to fall back to, so the closed set is exactly the two static instances,
        // checked here because this is the one place that enforces it (ClaimGate's own implicit
        // string conversion deliberately wraps anything).
        if (claimGate.HasValue
            && claimGate.Value is { } chosenGate
            && chosenGate != ClaimGate.Off
            && chosenGate != ClaimGate.TrackerAssignee)
        {
            throw new DomainValidationException(
                $"The claim gate must be {ClaimGate.Off} or {ClaimGate.TrackerAssignee} (whether a task "
                + "linked to a Jira card or a GitHub issue may be claimed on this install only while the "
                + "tracker shows that item assigned to this install's own tracker identity).");
        }

        // Each entry must name a CLI and carry actual text — an empty launch line is not a
        // clearing idiom here (unlike ContextLinks, there is no "the whole list of settings this
        // project needs" to be empty of; a launch-text entry that carries nothing is a mistake,
        // not an intentional absence), and Cli is stored normalized so a later `show --cli
        // Claude-Code` finds what `set --cli claude-code` wrote.
        if (launchTexts.HasValue)
        {
            foreach (LaunchText entry in launchTexts.Value ?? [])
            {
                if (entry.Cli.IsBlank())
                {
                    throw new DomainValidationException("A launch-text entry needs a CLI name.");
                }

                if (entry.Text.IsBlank())
                {
                    throw new DomainValidationException($"The launch text for '{entry.Cli}' cannot be blank.");
                }
            }

            launchTexts = Optional<IReadOnlyList<LaunchText>>.Of(
                [.. (launchTexts.Value ?? []).Select(entry => entry with { Cli = LaunchText.NormalizeCli(entry.Cli) })]);
        }

        // The same closed-set check every other rule in this decider runs (Decisions Log #141's
        // own reasoning): CloseoutEngine only ever compares this value against its own statics, so
        // an unrecognized one would silently read as Never rather than teach the operator about
        // the typo.
        if (closeLinkedIssue.HasValue
            && closeLinkedIssue.Value is { } chosenRule
            && chosenRule != CloseLinkedIssueRule.OnCloseout
            && chosenRule != CloseLinkedIssueRule.Never
            && chosenRule != CloseLinkedIssueRule.WhenAllTasksClose)
        {
            throw new DomainValidationException(
                $"The close-linked-issue rule must be {CloseLinkedIssueRule.OnCloseout}, "
                + $"{CloseLinkedIssueRule.Never}, or {CloseLinkedIssueRule.WhenAllTasksClose} (whether "
                + "true closeout closes a task's linked GitHub issue, and when — task: a task's linked "
                + "GitHub issue is closed at true closeout under a configurable rule).");
        }

        // Parsed here rather than merely wrapped, the BranchNameTemplate discipline: what lands on
        // the stream is what h9k project show prints back and what every composition prompt pastes
        // verbatim, so a text past the length bound or carrying a layout-override character is
        // refused at the command line instead of discovered in a prompt file nobody reads. Blank
        // records the platform default, which is what makes --writing-conventions "" a clear.
        if (writingConventions.HasValue)
        {
            writingConventions = Optional<WritingConventions>.Of(
                WritingConventions.Parse(writingConventions.Value));
        }

        // Trimmed and emptied of blanks the same way ContextLinks and VerifyCommands normalize
        // their own list input, so "epic, prd,, adr" and "epic,prd,adr" record identically and a
        // stray blank entry can never silently match every unlabeled issue.
        Optional<IReadOnlyList<string>> normalizedNeverCloseLabels = neverCloseLabels.HasValue
            ? Optional<IReadOnlyList<string>>.Of(
                [.. (neverCloseLabels.Value ?? []).Select(label => label.Trim()).Where(label => label.Length > 0)])
            : Optional<IReadOnlyList<string>>.None;

        return new ProjectSettingsChanged(
            project.Id,
            verifyCommands,
            skipPermissions,
            // The retired session-denominated ceiling (Decisions Log #140): never written again,
            // and there is no parameter left to write it with. Streams that recorded one replay
            // it unchanged, which is what h9k project show reads to name the retirement.
            Optional<int>.None,
            contextLinks,
            changedAt,
            changedByOwnerId,
            commitStyle,
            model,
            reviewRerequest,
            jiraProjectKey,
            homeDirectory,
            repositoryPath,
            backlogPolicy,
            backlogRoutingGuidance,
            maxComplianceReviewCycles,
            maxAdversarialReviewCycles,
            maxFinalFullPassRounds,
            lifetimeReviewCycleBudget,
            branchNameTemplate,
            normalizedComposition,
            ReviewStageCompositionValidation.AcknowledgmentActuallyNeeded(normalizedRaw, reviewStageCompositionAcknowledged),
            autoPrReview,
            // Clamped here, not merely trusted from the caller (conformance review, low): the
            // ReviewStageCompositionAcknowledged idiom two lines above is enforced at this exact
            // boundary rather than left to whichever caller happens to compute it correctly today,
            // so a future second caller of ChangeSettings — or a refactor of this one — cannot
            // write an unobserved acceptance to the stream by passing true on a change that
            // recorded no gate at all.
            AcceptedBrokenGate: acceptedBrokenGate && verifyCommands.HasValue,
            MaxParallelTasks: maxParallelTasks,
            Priority: priority,
            ClaimGate: claimGate,
            LaunchTexts: launchTexts,
            OrchestratorModel: orchestratorModel,
            CloseLinkedIssue: closeLinkedIssue,
            NeverCloseLabels: normalizedNeverCloseLabels,
            WritingConventions: writingConventions);
    }

    /// <summary>
    /// Archives a project on this install (task: a project can be archived, listed as archived,
    /// reactivated, and renamed). Pure and database-free like every decider method here — it
    /// refuses only what the aggregate itself already knows (already archived); the task-state
    /// refusal named in the acceptance criteria needs a query across this project's tasks, which
    /// only the CLI command can run, the same division <see cref="Register"/>'s duplicate-name
    /// check already draws with <c>ProjectAddCommand</c>.
    /// </summary>
    public static ProjectArchived Archive(
        ProjectAggregate project, string? reason, DateTimeOffset archivedAt, Guid archivedByOwnerId)
    {
        if (project.IsArchived)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is already archived"
                + (project.ArchivedAt is { } at ? $" (since {at:g})" : string.Empty)
                + $". Reactivate it: h9k project reactivate {project.Name}");
        }

        return new ProjectArchived(project.Id, reason.IsBlank() ? null : reason, archivedAt, archivedByOwnerId);
    }

    /// <summary>Ends an archive in place, on the same stream and the same id — the inverse of <see cref="Archive"/>.</summary>
    public static ProjectReactivated Reactivate(
        ProjectAggregate project, DateTimeOffset reactivatedAt, Guid reactivatedByOwnerId)
    {
        if (!project.IsArchived)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is not archived, so there is nothing to reactivate.");
        }

        return new ProjectReactivated(project.Id, reactivatedAt, reactivatedByOwnerId);
    }

    /// <summary>
    /// Changes a project's name only (task: a project can be archived, listed as archived,
    /// reactivated, and renamed) — NAME IS NOT AN IDENTIFIER, so nothing else on this project's
    /// stream, nor any task, run, or idea that references it by id, is affected. The duplicate-name
    /// check against every OTHER project's name is the caller's (<c>ProjectAddCommand</c>'s own
    /// check, factored out to <see cref="ProjectNameUniqueness"/>), the same division
    /// <see cref="Register"/>'s own duplicate check already draws.
    /// </summary>
    public static ProjectRenamed Rename(
        ProjectAggregate project, string newName, DateTimeOffset renamedAt, Guid renamedByOwnerId)
    {
        if (newName.IsBlank())
        {
            throw new DomainValidationException("A project needs a name; rename needs a new one to rename it to.");
        }

        if (newName.Equals(project.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException($"'{newName}' is already this project's name.");
        }

        return new ProjectRenamed(project.Id, project.Name, newName, renamedAt, renamedByOwnerId);
    }

    /// <summary>
    /// The repository path carries the same rule <see cref="ProjectHome"/> carries, and for the
    /// same reason: it is recorded once and read back by the daemon, which runs in no particular
    /// directory, so a relative path names a different repository for every process that resolves
    /// it. Callers resolve relative input themselves, where the current directory still means
    /// something; what reaches here unrooted is refused rather than recorded.
    /// <para>
    /// Origin incident (2026-08-23): the pre-PR review of the project-home branch found
    /// <c>h9k project add --no-home --repo-url …</c> composing the path from an empty home and
    /// recording <c>repo/&lt;name&gt;.git</c>, which the daemon would have resolved against its
    /// own working directory. The CLI refuses that combination now; this is the rule underneath
    /// it, so no other caller can reintroduce the same shape.
    /// </para>
    /// </summary>
    private static void RefuseRelativeRepositoryPath(string repositoryPath)
    {
        if (!Path.IsPathRooted(repositoryPath))
        {
            throw new DomainValidationException(
                $"'{repositoryPath}' is not an absolute path. A project's repository path is recorded "
                + "once and read back by the daemon, which runs in no particular directory, so a "
                + "relative path would name a different repository for every caller. Pass a full path.");
        }
    }
}
