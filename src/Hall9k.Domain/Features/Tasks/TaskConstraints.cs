namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Opt-in budget limits (PLAN.md §4 item 4). Null members mean "no limit" — and per
/// Decisions Log #11, no declared budget means nothing is ever auto-killed.
/// </summary>
public sealed record TaskConstraints(int? MaxTurns, long? MaxTokens, TimeSpan? MaxWallClock);
