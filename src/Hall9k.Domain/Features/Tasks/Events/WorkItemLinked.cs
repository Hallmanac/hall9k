namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The task is linked to an external work item that Hall9k read for itself
/// (h9k task link-jira, backlog 18). This is the observation gate's record, and every field on
/// it is an observation: the reference is the canonical key the external system answered with,
/// the title and status are what that system said about the item at <see cref="ObservedAt"/>,
/// and nothing here is refreshed afterwards.
/// <para>
/// The distinction that makes the event worth having is who is asserting. An agent that has just
/// created a card knows the key it believes it created; the platform knows what came back when
/// it asked. Those are different facts, and only the second one is recorded — the agent's claim
/// is an argument to the command, never the thing the command writes down.
/// </para>
/// <para>
/// It is also how the two funnel exits (PLAN.md §3.1a, §9.2) end up on the same field:
/// <c>--from-issue</c> adopts an item that already existed and records the reference on
/// <see cref="TaskAdded"/>; this records the reference for an item that came into existence
/// because of the task. Either way the task carries exactly one, for good.
/// </para>
/// </summary>
public sealed record WorkItemLinked(
    Guid Id,
    ExternalReference Reference,
    string ObservedTitle,
    string ObservedStatus,
    DateTimeOffset ObservedAt,
    DateTimeOffset LinkedAt,
    Guid LinkedByOwnerId);
