using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One project's effective membership-gate override and where it came from (security review idea
/// 6be68ee2, finding 1) — resolved the identical way <see cref="AutoPrReviewSetting"/> resolves its
/// own speed, straight off <see cref="ProjectSettingsHistory"/> rather than a stored projection
/// field, since a projection cannot tell a recorded choice from the field's own initialised
/// default. Deliberately project-only: unlike <see cref="AutoPrReviewSetting"/>, this reads only
/// <c>ProjectSettingsChanged</c>'s own half of the history and never
/// <c>ProjectTeamSettingsChanged</c> — an explicit override is this node's own convenience for now,
/// never replicated to a teammate's node, while the visibility-computed default it falls back to is
/// already fleet-consistent because every node reads <c>gh repo view</c> for itself.
/// </summary>
public sealed record ReviewMembershipGateSetting(ReviewMembershipPolicy Policy, bool Recorded)
{
    public static readonly ReviewMembershipGateSetting Unrecorded = new(ReviewMembershipPolicy.Unknown, Recorded: false);

    /// <summary>
    /// The explicit override the daemon's own membership gate takes as its <c>explicitSetting</c>
    /// parameter: true for on, false for off, null when nothing overrides its own fresh visibility
    /// read.
    /// </summary>
    public bool? ExplicitValue => Policy == ReviewMembershipPolicy.Enabled
        ? true
        : Policy == ReviewMembershipPolicy.Disabled ? false : null;

    /// <summary>How the effective value is described wherever origin is printed: 'explicit' or 'default'.</summary>
    public string Origin => Recorded ? "explicit" : "default";

    public static ReviewMembershipGateSetting From(ProjectSettingsHistory history) =>
        history.LastRecordedTeamField(
            change => change.ReviewRequiresMembership, _ => Optional<ReviewMembershipPolicy>.None) is { HasValue: true } recorded
            ? new ReviewMembershipGateSetting(recorded.Value ?? ReviewMembershipPolicy.Unknown, Recorded: true)
            : Unrecorded;

    public static async Task<ReviewMembershipGateSetting> ResolveAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken) =>
        From(await ProjectSettingsHistory.ReadAsync(session, projectId, cancellationToken));
}
