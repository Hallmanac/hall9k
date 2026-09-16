using Hall9k.Domain.Infrastructure.Extensions;

namespace Hall9k.Domain.Features.Project.Projections;

/// <summary>
/// Shared definition of "eligible for messaging" (idea 202383dc, M2): not archived, and with a
/// repository to actually flush, read, and squash a project-scoped outbox through. Both the daemon's
/// own message sweep and <c>h9k message send</c>'s own default-project resolution must agree on
/// this identically — an ineligible project can never be swept, so defaulting to or silently
/// including one in either place would only produce work nothing ever picks up.
/// </summary>
public static class ProjectEligibilityExtensions
{
    public static bool IsEligibleForMessaging(this ProjectDetails project) =>
        !project.IsArchived && project.RepositoryPath.IsNotBlank();
}
