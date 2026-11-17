namespace Hall9k.Domain.Features.Connection;

/// <summary>A refused claude-code connection's check passed again, so work bound to it starts and held runs resume.</summary>
public sealed record ConnectionClaudeCodeAccountCleared(
    Guid Id,
    DateTimeOffset ClearedAt);
