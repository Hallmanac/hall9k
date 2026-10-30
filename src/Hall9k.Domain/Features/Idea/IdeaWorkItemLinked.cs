using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// The idea carries a tracker item that Hall9k read for itself (<c>h9k idea add --from-linear</c>,
/// <c>--from-jira</c>, <c>--from-issue</c>). It has the fields of
/// <see cref="Hall9k.Domain.Features.Tasks.Events.WorkItemLinked"/> and means the same thing by them: the
/// reference is the canonical <c>&lt;provider&gt;:&lt;reference&gt;</c> the tracker answered with, and the
/// title, status, and time are what it said at <see cref="ObservedAt"/>, never refreshed afterwards.
/// <para>
/// It is its own event rather than a reuse of <c>WorkItemLinked</c> because the task projections apply
/// that one by stream id, and one on an idea's stream would vivify a task document under the idea's id.
/// It is written in the same transaction as <see cref="IdeaCaptured"/>, and a node that predates it skips
/// the event, so it holds the idea without the link.
/// </para>
/// </summary>
public sealed record IdeaWorkItemLinked(
    Guid Id,
    ExternalReference Reference,
    string ObservedTitle,
    string ObservedStatus,
    DateTimeOffset ObservedAt,
    DateTimeOffset LinkedAt,
    Guid LinkedByOwnerId);
