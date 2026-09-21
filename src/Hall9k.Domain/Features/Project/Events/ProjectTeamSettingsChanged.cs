using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// The team half of a project settings change (idea 202383dc, M2a): everything a teammate on a
/// different node needs to see the same way — the claim gate, the branch template, the backlog
/// policy, review settings, and writing conventions among them — split out of
/// <see cref="ProjectSettingsChanged"/> so it can travel as its own <c>ProjectScoped</c> event
/// while <see cref="ProjectSettingsChanged"/> itself stays <c>NodeScoped</c> (parallel caps, model
/// choices, orchestrator model, and this install's own filesystem paths).
/// <para>
/// Raised alongside <see cref="ProjectSettingsChanged"/>, never instead of it:
/// <see cref="ProjectDecider.ChangeSettings"/> keeps its own single-event shape and every one of
/// its ~80 call sites unchanged, and <see cref="From"/> derives this companion event from whatever
/// <see cref="ProjectDecider.ChangeSettings"/> already built, returning null when the change
/// touched no team field at all so a plain <c>--model</c> or <c>--home</c> change never appends an
/// empty companion event. <see cref="ProjectAggregate.Apply(ProjectTeamSettingsChanged)"/> sets the
/// identical properties <see cref="ProjectAggregate.Apply(ProjectSettingsChanged)"/> already sets
/// for these fields, so a replicated copy of this event updates a receiving node's own project
/// state the same way the original change updated the originating node's.
/// </para>
/// </summary>
public sealed record ProjectTeamSettingsChanged(
    Guid Id,
    DateTimeOffset ChangedAt,
    Guid ChangedByOwnerId,
    Optional<IReadOnlyList<VerifyCommand>> VerifyCommands = default,
    bool AcceptedBrokenGate = false,
    Optional<ClaimGate> ClaimGate = default,
    /// <summary>Who answers a cooperative claim request (idea 202383dc, item 5) — every teammate's node needs the same answer, so this travels with the team half.</summary>
    Optional<TakePolicy> TakePolicy = default,
    /// <summary>How long a cooperative take request waits for an answer (idea 202383dc, item 5) — see <see cref="ProjectSettingsChanged.TakeTimeoutMinutes"/>'s own doc.</summary>
    Optional<int?> TakeTimeoutMinutes = default,
    Optional<BranchNameTemplate> BranchNameTemplate = default,
    Optional<BacklogPolicy> BacklogPolicy = default,
    Optional<string> BacklogRoutingGuidance = default,
    Optional<ReviewRerequestPolicy> ReviewRerequest = default,
    Optional<int?> MaxComplianceReviewCycles = default,
    Optional<int?> MaxAdversarialReviewCycles = default,
    Optional<int?> MaxFinalFullPassRounds = default,
    Optional<int?> LifetimeReviewCycleBudget = default,
    Optional<ReviewStageComposition?> ReviewStageComposition = default,
    bool ReviewStageCompositionAcknowledged = false,
    Optional<WritingConventions> WritingConventions = default,
    Optional<JiraProjectKey> JiraProjectKey = default,
    Optional<CloseLinkedIssueRule> CloseLinkedIssue = default,
    Optional<IReadOnlyList<string>> NeverCloseLabels = default,
    Optional<AutoPrReviewSpeed> AutoPrReview = default,
    Optional<IReadOnlyList<ContextLink>> ContextLinks = default,
    Optional<CommitStyle> CommitStyle = default,
    /// <summary>
    /// Whether the designer persona's review drives this project's running product (idea
    /// b9b09779, piece 3) — a team field, because the review a teammate's own node dispatches
    /// has to read this pull request the same way whoever set it intended. See
    /// <see cref="ProjectSettingsChanged.DesignReviewDrive"/>'s own doc for the default and why
    /// <see cref="Project.ReviewDriveSetting"/> and not the projection resolves it.
    /// </summary>
    Optional<bool> DesignReviewDrive = default,
    /// <summary>
    /// Whether the QA persona's review drives this project's running product (idea b9b09779,
    /// piece 2) — a team field for the same reason <see cref="DesignReviewDrive"/> above is: the
    /// review a teammate's own node dispatches has to read this pull request the way whoever set
    /// it intended. See <see cref="ProjectSettingsChanged.QaReviewDrive"/>'s own doc for the
    /// default (off, the opposite of the designer's) and why <see cref="Project.ReviewDriveSetting"/>
    /// and not the projection resolves it.
    /// </summary>
    Optional<bool> QaReviewDrive = default)
{
    /// <summary>
    /// Builds the team companion from whatever <see cref="ProjectDecider.ChangeSettings"/> just
    /// built, or returns null when <paramref name="changed"/> touched no team field — the caller's
    /// signal to append nothing rather than a no-op event every plain node-settings change would
    /// otherwise add to the stream.
    /// </summary>
    public static ProjectTeamSettingsChanged? From(ProjectSettingsChanged changed)
    {
        bool anyTeamField = changed.VerifyCommands.HasValue
            || changed.ClaimGate.HasValue
            || changed.TakePolicy.HasValue
            || changed.TakeTimeoutMinutes.HasValue
            || changed.BranchNameTemplate.HasValue
            || changed.BacklogPolicy.HasValue
            || changed.BacklogRoutingGuidance.HasValue
            || changed.ReviewRerequest.HasValue
            || changed.MaxComplianceReviewCycles.HasValue
            || changed.MaxAdversarialReviewCycles.HasValue
            || changed.MaxFinalFullPassRounds.HasValue
            || changed.LifetimeReviewCycleBudget.HasValue
            || changed.ReviewStageComposition.HasValue
            || changed.WritingConventions.HasValue
            || changed.JiraProjectKey.HasValue
            || changed.CloseLinkedIssue.HasValue
            || changed.NeverCloseLabels.HasValue
            || changed.AutoPrReview.HasValue
            || changed.ContextLinks.HasValue
            || changed.CommitStyle.HasValue
            || changed.DesignReviewDrive.HasValue
            || changed.QaReviewDrive.HasValue;

        return anyTeamField
            ? new ProjectTeamSettingsChanged(
                changed.Id,
                changed.ChangedAt,
                changed.ChangedByOwnerId,
                changed.VerifyCommands,
                changed.AcceptedBrokenGate,
                changed.ClaimGate,
                changed.TakePolicy,
                changed.TakeTimeoutMinutes,
                changed.BranchNameTemplate,
                changed.BacklogPolicy,
                changed.BacklogRoutingGuidance,
                changed.ReviewRerequest,
                changed.MaxComplianceReviewCycles,
                changed.MaxAdversarialReviewCycles,
                changed.MaxFinalFullPassRounds,
                changed.LifetimeReviewCycleBudget,
                changed.ReviewStageComposition,
                changed.ReviewStageCompositionAcknowledged,
                changed.WritingConventions,
                changed.JiraProjectKey,
                changed.CloseLinkedIssue,
                changed.NeverCloseLabels,
                changed.AutoPrReview,
                changed.ContextLinks,
                changed.CommitStyle,
                changed.DesignReviewDrive,
                changed.QaReviewDrive)
            : null;
    }
}
