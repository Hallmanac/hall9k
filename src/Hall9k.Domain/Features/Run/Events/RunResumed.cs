namespace Hall9k.Domain.Features.Run.Events;

/// <summary>After an answer: new process, same claude session (exit-and-resume, log #5).</summary>
/// <param name="SessionName">
/// The name the resumed process actually spawned under. Ordinarily this is the same name
/// <see cref="RunDispatched.SessionName"/> already recorded (a resume re-enters the same
/// session), but a stream written before that field existed carries no recorded name, so the
/// resuming spawn computes a fallback of its own (<c>TokenBudgetRetryEngine</c>) — recording it
/// here rather than trusting the pre-existing (blank) value is what lets the projection report
/// the name the live process is actually answering to. Blank on a stream written before this
/// field existed.
/// </param>
public sealed record RunResumed(
    Guid Id,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset ResumedAt,
    string SessionName = "");
