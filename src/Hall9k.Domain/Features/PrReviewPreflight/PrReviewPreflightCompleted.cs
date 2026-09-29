namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// What a pull-request review pre-flight came to (idea 6be68ee2, finding 1, phase one).
/// <see cref="Safe"/> is read off the session's own terminal marker (<c>PREFLIGHT: safe|unsafe -
/// &lt;reason&gt;</c>), parsed by reusing <c>ReviewResultParser.LastMarkerValue</c>: anything the
/// marker does not read as exactly "safe" is unsafe, including a missing marker entirely — a
/// pre-flight that cannot be read as safe is never treated as one. This is a layer on top of the
/// membership gate and the permission file, never the lock: the list it read only points a
/// session's attention, and it is not a classifier.
/// <para>
/// Appended only on a real parse. A budget exhaustion, a launch failure, a wall-clock timeout, or
/// a daemon restart that found the process dead never reaches this event at all — those leave the
/// pre-flight's own stream permanently without a <see cref="PrReviewPreflightCompleted"/>, and the
/// task is requeued for a fresh pre-flight instead (<c>RunSupervisor.AbandonPreflightAsync</c>), so
/// every row this event lands on is a genuine verdict, safe or unsafe.
/// </para>
/// </summary>
public sealed record PrReviewPreflightCompleted(
    Guid Id,
    bool Safe,
    string Verdict,
    string Reason,
    DateTimeOffset CompletedAt);
