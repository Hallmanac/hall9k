namespace Hall9k.Domain.Features.Project;

/// <summary>
/// The grace period between <c>h9k project remove --purge</c> scheduling a permanent hard delete
/// and the daemon sweep that carries it out (task: an archived project can be purged; PLAN.md §16
/// #182's purge follow-up). Fixed, not a per-install setting — this is a safety window against a
/// mistaken purge, not a policy knob an operator tunes.
/// </summary>
public static class ProjectPurge
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);
}
