namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed unresolved Copilot review threads on the run's pull
/// request. A follow-up dispatch on the resolve-copilot-reviews path (or a
/// CloseoutParked, when the automatic budget is spent) is appended in the same
/// transaction.
/// </summary>
public sealed record ReviewFeedbackReceived(
    Guid Id,
    int UnresolvedThreadCount,
    DateTimeOffset ObservedAt);
