using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One in-flight review pass of the current cycle (Decisions Log #59), as the run stream
/// records it: the lens it looks through, the identity the daemon needs to adopt it after a
/// restart (PID plus start time, the log #2 reuse guard), and the model it was spawned on.
/// <para>
/// SessionId names THIS leg's artifacts; TranscriptSessionId is the session that holds the
/// review conversation. They are the same for a fresh pass and differ for a verdict
/// re-prompt, which resumes the original session under a new artifact identity so the
/// resumed leg's stream file never collides with the original's.
/// </para>
/// </summary>
public sealed record ReviewPassSession(
    ReviewLens Lens,
    Guid SessionId,
    Guid TranscriptSessionId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    AgentModel Model,
    ReviewMode Mode);
