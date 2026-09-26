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
        Optional<TakePolicy> takePolicy = default,
        Optional<int?> takeTimeoutMinutes = default,
        Optional<IReadOnlyList<LaunchText>> launchTexts = default,
        Optional<AgentModel> orchestratorModel = default,
        Optional<CloseLinkedIssueRule> closeLinkedIssue = default,
        Optional<IReadOnlyList<string>> neverCloseLabels = default,
        Optional<WritingConventions> writingConventions = default,
        Optional<WorkItemProvider> primaryTracker = default,
        Optional<OrchestratorFeedLevel> orchestratorFeed = default,
        Optional<int?> courierMaxWaitSeconds = default,
        Optional<bool> designReviewDrive = default,
        Optional<bool> qaReviewDrive = default,
        Optional<IReadOnlyList<string>> nonExecutablePaths = default,
        Optional<AgentEffort> effort = default)
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

        // The BacklogPolicy idiom again: Unknown is the closed set's own "no default" value, so the
        // set actually checked is exactly the three static instances a primary tracker can be —
        // Unknown to clear, or one of the two real providers a task's primary reference can name.
        // GitHubPullRequest is refused along with everything else: a primary tracker is a backlog
        // item's home, never a pull request under review.
        if (primaryTracker.HasValue
            && primaryTracker.Value is { } chosenTracker
            && chosenTracker != WorkItemProvider.Unknown
            && chosenTracker != WorkItemProvider.GitHub
            && chosenTracker != WorkItemProvider.Jira)
        {
            throw new DomainValidationException(
                $"The primary tracker must be {WorkItemProvider.GitHub} or {WorkItemProvider.Jira} "
                + "(which reference wins when h9k task add adopts both a GitHub issue and a Jira card "
                + "and the two are not resolved by --primary-tracker there); 'none' clears the default.");
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

        // The identical closed-set discipline as ClaimGate just above, for the orchestrator
        // feed's own band (idea 89471598, piece 2). OrchestratorFeedLevel's implicit string
        // conversion deliberately wraps anything, so this is the one place the three bands are
        // actually enforced — and an unrecognized band would read at the default's breadth,
        // silently giving an operator a feed they did not ask for.
        if (orchestratorFeed.HasValue
            && orchestratorFeed.Value is { } chosenFeedLevel
            && !OrchestratorFeedLevel.All.Contains(chosenFeedLevel))
        {
            throw new DomainValidationException(
                $"The orchestrator feed level must be one of "
                + $"{string.Join(", ", OrchestratorFeedLevel.All.Select(level => level.Value))} (how much of "
                + "this project's own history h9k orchestrator feed hands a window).");
        }

        // The identical closed-set discipline as ClaimGate just above, for the other setting idea
        // 202383dc, item 5 introduces: who answers a cooperative claim request.
        if (takePolicy.HasValue
            && takePolicy.Value is { } chosenTakePolicy
            && chosenTakePolicy != Project.TakePolicy.Auto
            && chosenTakePolicy != Project.TakePolicy.Ask)
        {
            throw new DomainValidationException(
                $"The take policy must be {Project.TakePolicy.Auto} or {Project.TakePolicy.Ask} (who "
                + "answers a member's cooperative claim request — the holder's own node on receipt, or "
                + "the holder's own human through h9k task grant/refuse).");
        }

        // Present-with-null clears the override back to the platform default, the same idiom
        // MaxParallelTasks uses; a negative or zero value has nothing sensible to wait for.
        if (takeTimeoutMinutes is { HasValue: true, Value: { } timeoutMinutes } && timeoutMinutes <= 0)
        {
            throw new DomainValidationException(
                $"TakeTimeoutMinutes must be greater than 0, got {timeoutMinutes}. It is how long a "
                + "cooperative take request waits for an answer before h9k task take names --force as "
                + "the way on; 'default' clears the override back to the platform default (30 minutes).");
        }

        // Present-with-null clears the override back to the platform default, the same idiom
        // TakeTimeoutMinutes uses just above. Zero is legal here, unlike that one: a ceiling of
        // zero seconds is the deliberate "never batch, always immediate" extreme (idea 89471598,
        // piece 3), not a value with nothing sensible to wait for.
        if (courierMaxWaitSeconds is { HasValue: true, Value: { } maxWaitSeconds } && maxWaitSeconds < 0)
        {
            throw new DomainValidationException(
                $"CourierMaxWaitSeconds must be 0 or more, got {maxWaitSeconds}. It is the ceiling the feed "
                + "courier's own batching wait ramps toward the busier the project's feed gets; 'default' "
                + $"clears the override back to the platform default ({ProjectAggregate.DefaultCourierMaxWaitSeconds} seconds).");
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

        // Trimmed and emptied of blanks, the identical NeverCloseLabels idiom just above: this is
        // always this project's own ADDITIONS to NonExecutablePathDefaults.Rules, never a
        // replacement of them — there is no parameter here that could remove one of the compiled
        // defaults, which is what makes "a project can only add to the set" hold by construction.
        // A leading '!' is refused outright rather than merely trusted to stay well-behaved
        // (independent pre-PR review, cycle 1, conformance lens, medium): NonExecutablePathClassifier
        // honors an exclusion from ANY rule in the effective list, project additions included, so
        // an unchecked '--non-executable-path "!docs/"' would silently narrow a compiled default
        // despite "by construction" describing only the list shape, never what a rule inside it can
        // do. Exclusion syntax stays reserved for the compiled set, where NonExecutablePathDefaults
        // itself is the only writer.
        if (nonExecutablePaths.HasValue
            && (nonExecutablePaths.Value ?? []).Any(glob => glob.Trim().StartsWith('!')))
        {
            throw new DomainValidationException(
                "A non-executable-path addition cannot start with '!': exclusion syntax is reserved "
                + "for the compiled default set, so a project may only add to it, never narrow it.");
        }

        Optional<IReadOnlyList<string>> normalizedNonExecutablePaths = nonExecutablePaths.HasValue
            ? Optional<IReadOnlyList<string>>.Of(
                [.. (nonExecutablePaths.Value ?? []).Select(glob => glob.Trim()).Where(glob => glob.Length > 0)])
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
            TakePolicy: takePolicy,
            TakeTimeoutMinutes: takeTimeoutMinutes,
            LaunchTexts: launchTexts,
            OrchestratorModel: orchestratorModel,
            CloseLinkedIssue: closeLinkedIssue,
            NeverCloseLabels: normalizedNeverCloseLabels,
            WritingConventions: writingConventions,
            PrimaryTracker: primaryTracker,
            OrchestratorFeed: orchestratorFeed,
            CourierMaxWaitSeconds: courierMaxWaitSeconds,
            DesignReviewDrive: designReviewDrive,
            QaReviewDrive: qaReviewDrive,
            NonExecutablePaths: normalizedNonExecutablePaths,
            Effort: effort);
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

    /// <summary>
    /// Ends an archive in place, on the same stream and the same id — the inverse of
    /// <see cref="Archive"/>. Refused while a purge is pending (<see cref="SchedulePurge"/>): this
    /// is the fence itself, and it is the reason <c>ProjectPurgeEngine</c>'s own liveness re-check
    /// (defense in depth, for any writer that reactivates without going through this method fenced)
    /// almost never fires — without this refusal, a project reactivated out from under a
    /// still-pending deadline would be destroyed the next time the sweep runs. Cancel the purge
    /// first (<c>h9k project cancel-purge</c>), which is exactly what leaves a project reactivatable
    /// again.
    /// </summary>
    public static ProjectReactivated Reactivate(
        ProjectAggregate project, DateTimeOffset reactivatedAt, Guid reactivatedByOwnerId)
    {
        if (!project.IsArchived)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' is not archived, so there is nothing to reactivate.");
        }

        if (project.PurgeAt is { } pendingDeadline)
        {
            // Local time, not raw UTC: every other place this deadline reaches an operator
            // (h9k project show, h9k project list --include-archived, the remove/purge messages)
            // prints it in local time, and a raw UTC instant here would read as a different
            // deadline for the same purge with no offset on either to tell them apart
            // (independent pre-PR review, cycle 1, conformance lens).
            throw new DomainValidationException(
                $"Project '{project.Name}' has a purge scheduled for {pendingDeadline.ToLocalTime():g}. "
                + $"Cancel it first: h9k project cancel-purge {project.Name}. Reactivating a project the "
                + "sweep would still destroy is refused rather than left to race the deadline.");
        }

        return new ProjectReactivated(project.Id, reactivatedAt, reactivatedByOwnerId);
    }

    /// <summary>
    /// Schedules a permanent hard delete of an archived project's database footprint (task: an
    /// archived project can be purged — the second half of the two-tier project-removal design,
    /// PLAN.md §16 #182's purge follow-up). Pure and database-free like every decider method
    /// here: the scope this purge will destroy (how many tasks, runs, and ideas) is a database
    /// query only the CLI can run, the same division <see cref="Archive"/>'s own task-state
    /// refusal already draws.
    /// </summary>
    public static ProjectPurgeScheduled SchedulePurge(
        ProjectAggregate project, DateTimeOffset scheduledAt, Guid scheduledByOwnerId, TimeSpan? gracePeriod = null)
    {
        if (!project.IsArchived)
        {
            throw new DomainValidationException(
                $"Project '{project.Name}' must be archived before it can be purged; "
                + "h9k project remove --purge does both together.");
        }

        if (project.PurgeAt is { } existingDeadline)
        {
            // Local time, the same fix and the same reason as Reactivate's own refusal above.
            throw new DomainValidationException(
                $"Project '{project.Name}' already has a purge scheduled for {existingDeadline.ToLocalTime():g}. "
                + $"Cancel it first: h9k project cancel-purge {project.Name}");
        }

        TimeSpan period = gracePeriod ?? ProjectPurge.GracePeriod;
        return new ProjectPurgeScheduled(project.Id, scheduledAt, scheduledAt + period, scheduledByOwnerId);
    }

    /// <summary>Ends a pending purge before it fires, on the same stream — the inverse of <see cref="SchedulePurge"/>. Leaves the project archived, never reactivated.</summary>
    public static ProjectPurgeCancelled CancelPurge(
        ProjectAggregate project, DateTimeOffset cancelledAt, Guid cancelledByOwnerId)
    {
        if (project.PurgeAt is null)
        {
            throw new DomainValidationException($"Project '{project.Name}' has no purge scheduled.");
        }

        return new ProjectPurgeCancelled(project.Id, cancelledAt, cancelledByOwnerId);
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
    /// This install's own GitHub role on the project's repository, as just read from the
    /// repository object itself — or null when nothing about it changed since the last
    /// observation (idea 202383dc, A2b, item 3). Racing another door that observed the identical
    /// role in the same instant is harmless, the same reasoning
    /// <c>ConnectionDecider.ObserveGitHubIdentity</c> and <c>TrackerClaimGate</c>'s own Jira
    /// identity append already rely on — the value is identical, the stream simply carries two
    /// identical observations.
    /// </summary>
    public static ProjectGitHubAccessObserved? ObserveGitHubAccess(
        ProjectAggregate project, long accountId, string login, GitHubRepositoryRole role, DateTimeOffset observedAt)
    {
        if (login.IsBlank())
        {
            throw new DomainValidationException("A GitHub access observation requires the login it was read as.");
        }

        return project.GitHubOwnAccountId == accountId && project.GitHubOwnLogin == login && project.GitHubOwnRole == role
            ? null
            : new ProjectGitHubAccessObserved(project.Id, accountId, login, role, observedAt);
    }

    /// <summary>
    /// The project's GitHub collaborator list with roles, as just read — or null when the set of
    /// (account, role) pairs is identical to the last observation, order ignored: GitHub's own
    /// list order carries no meaning, and reordering the identical membership must never look like
    /// a change. The aggregate's own unobserved default is also an empty list, so a genuinely
    /// empty roster's first-ever observation is let through on <see cref="ProjectAggregate.GitHubCollaboratorsObservedAt"/>
    /// alone — otherwise it would compare equal to the default and never append, leaving that
    /// sentinel stuck at "never observed" for a repository that was, in fact, just read as having
    /// no collaborators.
    /// </summary>
    public static ProjectGitHubCollaboratorsObserved? ObserveGitHubCollaborators(
        ProjectAggregate project, IReadOnlyList<GitHubCollaboratorRole> collaborators, DateTimeOffset observedAt)
    {
        HashSet<GitHubCollaboratorRole> before = [.. project.GitHubCollaborators];
        HashSet<GitHubCollaboratorRole> after = [.. collaborators];
        return project.GitHubCollaboratorsObservedAt is not null && before.SetEquals(after)
            ? null
            : new ProjectGitHubCollaboratorsObserved(project.Id, collaborators, observedAt);
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

    public static MemberVouched VouchMember(
        Guid projectId, string rootFingerprint, ProjectMemberRole role, DateTimeOffset issuedAt)
    {
        if (rootFingerprint.IsBlank())
        {
            throw new DomainValidationException("A project membership needs the root fingerprint it is vouching.");
        }

        if (role != ProjectMemberRole.Owner && role != ProjectMemberRole.Member)
        {
            throw new DomainValidationException("A project member's role must be owner or member (idea 202383dc).");
        }

        return new MemberVouched(projectId, rootFingerprint, role, issuedAt);
    }

    public static MemberRemoved RemoveMember(Guid projectId, string rootFingerprint, DateTimeOffset removedAt)
    {
        if (rootFingerprint.IsBlank())
        {
            throw new DomainValidationException("A project member removal needs the root fingerprint being removed.");
        }

        return new MemberRemoved(projectId, rootFingerprint, removedAt);
    }

    /// <summary>
    /// How long a project's own prompt-builder addendum may be before <c>--over-cap</c> is needed
    /// (idea b9b09779, piece 6). Generous for real house guidance, and bounded because the text is
    /// pasted verbatim after every prompt this builder composes — the same reasoning and the same
    /// number as <see cref="WritingConventions.MaximumLength"/>.
    /// </summary>
    public const int PromptAddendumMaximumLength = 4000;

    /// <summary>
    /// Replaces the whole addendum a project states for one prompt builder. A judgment call, never
    /// a hard blocker: past <see cref="PromptAddendumMaximumLength"/> this refuses unless
    /// <paramref name="overCap"/> is set with <paramref name="overCapReason"/> stated, the same
    /// "acknowledge the consequence" idiom <c>--accept-reduced-review</c> already uses.
    /// </summary>
    public static ProjectPromptAddendumSet SetPromptAddendum(
        Guid projectId, PromptBuilderKey builder, string? content, bool overCap, string? overCapReason,
        Guid setByOwnerId, DateTimeOffset setAt)
    {
        if (builder == PromptBuilderKey.Unknown)
        {
            throw new DomainValidationException("An addendum needs a real prompt builder key to set it on.");
        }

        string trimmed = content?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                $"An addendum needs content — h9k project prompt-addendum remove {builder} clears one instead.");
        }

        bool exceedsCap = trimmed.Length > PromptAddendumMaximumLength;
        if (overCap && !exceedsCap)
        {
            throw new DomainValidationException(
                $"--over-cap has nothing to acknowledge: this addendum is {trimmed.Length} characters, "
                + $"under the {PromptAddendumMaximumLength}-character cap.");
        }

        if (overCap && overCapReason.IsBlank())
        {
            throw new DomainValidationException(
                "--over-cap needs the reason this addendum has to stay past the cap, recorded on the event: "
                + $"h9k project prompt-addendum set {builder} --file <path> --over-cap \"<why>\".");
        }

        if (exceedsCap && !overCap)
        {
            throw new DomainValidationException(
                $"This addendum is {trimmed.Length} characters, past the {PromptAddendumMaximumLength}-character "
                + $"cap. It is pasted verbatim after {builder}'s own rules section on every prompt it composes, "
                + "so a long one spends the session's context rather than steering it. Tighten it, or accept the "
                + $"cost: h9k project prompt-addendum set {builder} --file <path> --over-cap \"<why this needs the room>\".");
        }

        return new ProjectPromptAddendumSet(
            projectId, builder, trimmed, overCap, overCap ? overCapReason!.Trim() : null, setAt, setByOwnerId);
    }

    public static ProjectPromptAddendumRemoved RemovePromptAddendum(
        Guid projectId, PromptBuilderKey builder, Guid removedByOwnerId, DateTimeOffset removedAt)
    {
        if (builder == PromptBuilderKey.Unknown)
        {
            throw new DomainValidationException("An addendum removal needs a real prompt builder key.");
        }

        return new ProjectPromptAddendumRemoved(projectId, builder, removedAt, removedByOwnerId);
    }

    /// <summary>
    /// Records this install's own local mirror of the project's ledger-derived key (idea 202383dc,
    /// M2). Stateless like <see cref="VouchMember"/>: the caller (<c>h9k project join</c> or
    /// <c>h9k project assign-key</c>) already knows whether this project's own <c>ProjectDetails.ProjectKey</c>
    /// is unset or already matches, and only calls this when there is something new to record — this
    /// method's own job is only the shape of a 26-character Crockford-base32 ULID, never re-deriving
    /// the caller's own idempotency check.
    /// </summary>
    public static ProjectKeyAssigned AssignKey(Guid projectId, string projectKey, DateTimeOffset assignedAt)
    {
        // Ulid.TryParse checks the full Crockford-base32 shape, not merely the length: a
        // length-only check let a 26-character string containing anything at all (Spectre markup
        // included) through as a "project key", which h9k project show would then hand straight to
        // AnsiConsole.MarkupLine and crash on (independent pre-PR review, cycle 1, adversarial
        // lens, low).
        if (!Ulid.TryParse(projectKey, out _))
        {
            throw new DomainValidationException(
                $"'{projectKey}' is not a project key — a project key is the 26-character Crockford-base32 "
                + "ULID minted once at genesis (idea 202383dc, M2), never a fingerprint or any other id.");
        }

        return new ProjectKeyAssigned(projectId, projectKey, assignedAt);
    }

    /// <summary>
    /// How long a project's run skill may be (idea b9b09779, piece 4). Generous for a real
    /// procedure with commands and citations in it, and bounded because a QA or design review
    /// session reads the whole thing on its first turn before it does anything else — the same
    /// reasoning and the same tier as <see cref="PromptAddendumMaximumLength"/>, one size up
    /// because a full-text run skill legitimately carries more than house guidance does.
    /// </summary>
    public const int RunSkillMaximumLength = 12000;

    /// <summary>
    /// Asks for this project's run skill to be discovered: <c>h9k project add</c> at registration
    /// and <c>h9k project set --discover-run-skill</c> both come through here. Deliberately
    /// unconditional on what already exists — re-running discovery over a repository whose launch
    /// story has changed is exactly what the command is for, and the daemon's own survey is what
    /// decides whether a session is worth dispatching at all.
    /// </summary>
    public static ProjectRunSkillDiscoveryRequested RequestRunSkillDiscovery(
        Guid projectId, Guid requestedByOwnerId, DateTimeOffset requestedAt) =>
        new(projectId, requestedAt, requestedByOwnerId);

    /// <summary>
    /// Records the run skill as it now stands — the one event every route goes through (a
    /// discovery session's composed markdown handed back through the daemon, the daemon's own
    /// none-discoverable record, and <c>h9k project run-skill set --file</c>). The shared shape is
    /// enforced here rather than at each call site, so a hand-set file and a session's answer are
    /// held to the identical contract: <see cref="RunSkillDocument.Headings"/> all present, a real
    /// shape, and a length a reading session can afford.
    /// </summary>
    public static ProjectRunSkillRecorded RecordRunSkill(
        Guid projectId, string? content, RunSkillShape shape, RunSkillAuthor author, string? composedAgainstCommit,
        Guid recordedByOwnerId, DateTimeOffset recordedAt)
    {
        if (shape == RunSkillShape.Unknown)
        {
            throw new DomainValidationException(
                "A run skill needs a real shape: "
                + $"{string.Join(", ", RunSkillShape.All.Select(known => known.Value))}.");
        }

        if (author == RunSkillAuthor.Unknown)
        {
            throw new DomainValidationException(
                "A run skill needs a real author — who composed it is an audit fact, never left blank.");
        }

        string trimmed = content?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "A run skill needs content. A repository with no discoverable way to run it gets the "
                + "none-discoverable skill, which says so in full, rather than an empty one.");
        }

        if (RunSkillDocument.MissingHeadings(trimmed) is { Count: > 0 } missing)
        {
            throw new DomainValidationException(
                $"This run skill is missing {string.Join(", ", missing)}. Every project's run skill carries the "
                + $"same six sections in the same order ({string.Join(", ", RunSkillDocument.Headings)}), so a "
                + "reader standing any project up reads the same document. Add the missing heading(s), even if "
                + "the section's honest content is that there are none.");
        }

        if (trimmed.Length > RunSkillMaximumLength)
        {
            throw new DomainValidationException(
                $"This run skill is {trimmed.Length} characters, past the {RunSkillMaximumLength}-character cap. "
                + "A review session reads the whole thing before it does anything else, so a long one spends "
                + "its context rather than standing the project up. Point at the repository's own files by path "
                + "instead of restating them.");
        }

        return new ProjectRunSkillRecorded(
            projectId, trimmed, shape, composedAgainstCommit?.Trim() ?? string.Empty, author, recordedAt,
            recordedByOwnerId);
    }

    /// <summary>
    /// Records that a discovery produced nothing usable. The reason is required: an unexplained
    /// failure is one a human cannot act on, and <c>h9k project show</c> prints it verbatim.
    /// </summary>
    public static ProjectRunSkillDiscoveryFailed FailRunSkillDiscovery(
        Guid projectId, string? reason, DateTimeOffset failedAt)
    {
        if (reason.IsBlank())
        {
            throw new DomainValidationException(
                "A failed run-skill discovery needs the reason it failed, recorded on the event.");
        }

        return new ProjectRunSkillDiscoveryFailed(projectId, reason.Trim(), failedAt);
    }
}
