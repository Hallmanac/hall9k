namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// <c>h9k task run-local &lt;task&gt; --continue</c>: the reviewer did the human step and the
/// launch picks up at <see cref="FromStepNumber"/> (idea b9b09779, piece 5).
/// <para>
/// The platform never checks that the human step was actually done, and does not pretend to: the
/// reviewer saying they did it is the only observation available, and this event records that they
/// said so rather than that it happened.
/// </para>
/// </summary>
/// <param name="Walker">
/// The <c>h9k</c> process about to walk the rest of the plan, replacing the one that walked up to
/// the pause — a resume arrives in a different process minutes or days later, and the launch's own
/// record has to name the one actually walking it now (<see cref="LocalLaunchStarted.Walker"/>).
/// Null on an event written before the walker was recorded.
/// </param>
public sealed record LocalLaunchResumed(
    Guid Id,
    Guid LaunchId,
    int FromStepNumber,
    LocalLaunchProcess? Walker,
    DateTimeOffset ResumedAt,
    Guid ResumedByOwnerId);
