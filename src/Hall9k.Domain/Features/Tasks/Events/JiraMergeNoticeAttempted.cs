namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The deferred merge notice from <see cref="JiraMergeNoticeQueued"/> was attempted — clears the
/// queue marker regardless of what the attempt itself came to, because that outcome lands on the
/// ordinary Jira write event trail (<see cref="JiraWriteRequested"/>, <see cref="JiraWriteSucceeded"/>,
/// <see cref="JiraWriteFailed"/>) exactly as any other write's does; this event only ever says the
/// queue was drained, never what answer Jira gave.
/// </summary>
public sealed record JiraMergeNoticeAttempted(Guid TaskId, DateTimeOffset AttemptedAt);
