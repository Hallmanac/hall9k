namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The intent, recorded before anything reaches Jira (Brian's design, 2026-08-28: the write-audit
/// scope records intent, payload, and outcome for every interaction hall9k has with Jira, never a
/// mirror of card state). <see cref="PayloadJson"/> is the composed payload exactly as submitted —
/// whatever an agent's or an operator's judgment produced — so a failed or a retried attempt can
/// be told from the request it belongs to, and so the record survives even a write that never
/// reaches Jira at all.
/// </summary>
public sealed record JiraWriteRequested(
    Guid TaskId,
    Guid WriteId,
    JiraWriteOperation Operation,
    string? IssueKey,
    string PayloadJson,
    Guid RequestedByOwnerId,
    DateTimeOffset RequestedAt);
