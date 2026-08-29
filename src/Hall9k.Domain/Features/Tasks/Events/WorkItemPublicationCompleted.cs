namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The publication session ended, and whether the task came out of it carrying a link.
/// <para>
/// Linked is read off the task's own state rather than off anything the agent said, which is
/// the whole point of the observation gate: a session that reported success but never got a
/// key past <c>h9k task write-jira</c> completed without a link, and the record says so.
/// Outcome is the human-readable why — the session's own last words, the timeout, or the exit
/// that left nothing behind — kept because "no link" on its own tells nobody what to do next.
/// </para>
/// </summary>
public sealed record WorkItemPublicationCompleted(
    Guid Id,
    bool Linked,
    string? Outcome,
    DateTimeOffset CompletedAt);
