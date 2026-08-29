namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The operator's attached Claude Code session exited — the process the CLI spawned for
/// <see cref="InteractiveSessionStarted"/> returned control to `h9k task work`. Usage is
/// whatever is observable at exit, null for what is not (the same nullable-Turns convention
/// <c>StreamJsonParser.AgentResult.Turns</c> already follows for headless runs): an interactive
/// session is attached to the operator's terminal rather than driven headlessly through
/// `--output-format stream-json`, so there is no result payload to read usage off, and every
/// field here is honestly null unless a future build finds a way to observe it — never guessed
/// at as zero.
/// </summary>
public sealed record InteractiveSessionEnded(
    Guid Id,
    Guid ClaudeSessionId,
    DateTimeOffset EndedAt,
    int? Turns,
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd);
