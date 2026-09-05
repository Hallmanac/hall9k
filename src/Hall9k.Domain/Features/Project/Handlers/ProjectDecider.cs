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
        ProjectHome? homeDirectory = null)
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
            homeDirectory ?? ProjectHome.None);
    }

    public static ProjectSettingsChanged ChangeSettings(
        ProjectAggregate project,
        Optional<IReadOnlyList<VerifyCommand>> verifyCommands,
        Optional<bool> skipPermissions,
        Optional<int> maxParallelAgents,
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
        bool acceptedBrokenGate = false)
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

        if (maxParallelAgents.HasValue && maxParallelAgents.Value < 1)
        {
            throw new DomainValidationException("MaxParallelAgents must be at least 1.");
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

        return new ProjectSettingsChanged(
            project.Id,
            verifyCommands,
            skipPermissions,
            maxParallelAgents,
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
            AcceptedBrokenGate: acceptedBrokenGate && verifyCommands.HasValue);
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
