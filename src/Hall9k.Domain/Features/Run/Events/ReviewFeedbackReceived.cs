namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor observed unresolved review threads on the run's pull request,
/// from any reviewer — Copilot, a teammate, or the pull request's own author leaving
/// themselves a note (Decisions Log #62). A follow-up dispatch on the resolve-review-threads
/// path (or a CloseoutParked, when the automatic budget is spent) is appended in the same
/// transaction.
/// </summary>
/// <param name="UnresolvedThreadCount">Every unresolved thread, whoever started it.</param>
/// <param name="UnresolvedHumanThreadCount">
/// How many of those threads a human started, which is what makes a follow-up's care level
/// answerable from the stream rather than from the prompt alone. Null on events written
/// before reviewers other than Copilot were counted: those observations never looked at
/// authorship this way, and zero would claim they had (the never-guess rule).
/// </param>
public sealed record ReviewFeedbackReceived(
    Guid Id,
    int UnresolvedThreadCount,
    DateTimeOffset ObservedAt,
    int? UnresolvedHumanThreadCount = null);
