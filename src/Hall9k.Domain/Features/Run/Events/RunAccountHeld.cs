namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A spawn for this run was refused because its Claude Code account's check failed, so the run waits rather
/// than fails, exactly as <see cref="RunLaunchHeld"/> waits on the node: the leg that was about to start is
/// cleared the same way, and the run resumes through the same re-entry paths once
/// <see cref="Connection.ConnectionClaudeCodeAccountCleared"/> lands for <see cref="ConnectionId"/>. Unlike a
/// launch hold it holds only this account's work; the node-wide hold never counts it.
/// <see cref="PrimarySession"/> says the refused spawn was the run's primary session (its launch, its
/// error-result retry, or a zero-work exit re-checked as a refusal). Such a run resumes by starting that session
/// again, even when the same commit already moved it to Verifying, rather than at the gates it never reached.
/// </summary>
public sealed record RunAccountHeld(
    Guid Id,
    Guid ConnectionId,
    string Reason,
    DateTimeOffset HeldAt,
    bool PrimarySession = false);
