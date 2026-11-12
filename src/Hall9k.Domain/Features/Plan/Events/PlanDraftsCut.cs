namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// <c>h9k plan pursue</c>: a person answered the gate by pursuing the idea, and the report's proposed tasks were cut as
/// drafts in the same save. Appended once; every step after is a plan's ordinary path from refinement, and the only drafts the
/// plan gains later are those an accepted split cuts (<see cref="PlanSplitAccepted"/>). The stream's version is the fence that makes two pursues cut one set of drafts.
/// </summary>
/// <param name="Id">The plan's id.</param>
/// <param name="ProjectId">The project every draft belongs to.</param>
/// <param name="IdeaId">The idea the drafts were cut from.</param>
/// <param name="DraftIds">The cut drafts, in the order the report proposed them.</param>
/// <param name="EpicId">The epic <c>--epic-title</c> created for them, or null when none was asked for.</param>
/// <param name="NoExistingItemAttested">The <c>--no-existing-item</c> attestation the plan's publish passes on.</param>
/// <param name="UntrackedAttested">The <c>--untracked</c> attestation the plan's publish passes on.</param>
/// <param name="CutByOwnerId">Who pursued, who gave any attestation, and who is recorded as the publisher.</param>
/// <param name="CutAt">When.</param>
public sealed record PlanDraftsCut(
    Guid Id,
    Guid ProjectId,
    Guid IdeaId,
    IReadOnlyList<Guid> DraftIds,
    Guid? EpicId,
    bool NoExistingItemAttested,
    bool UntrackedAttested,
    Guid CutByOwnerId,
    DateTimeOffset CutAt);
