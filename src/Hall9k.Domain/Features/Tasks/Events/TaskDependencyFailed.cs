namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A blocker reached Failed or Abandoned, so it will never close out on its own (Decisions
/// Log #34). The dependent stays Blocked and reads as NeedsHuman: unblocking it silently
/// would dispatch work whose premise died, and saying nothing would strand it. The recorded
/// dependency is still a real edge — retrying or resolving it clears this the honest way.
/// </summary>
public sealed record TaskDependencyFailed(
    Guid Id,
    Guid DependencyId,
    string Reason,
    DateTimeOffset ObservedAt);
