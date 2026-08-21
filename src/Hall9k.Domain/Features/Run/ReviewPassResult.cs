using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One finished review pass of the current cycle (Decisions Log #59): the lens, the verdict
/// it reached, and the session that reached it. SessionId is the resume target for the one
/// verdict re-prompt, and Model is the model that session already runs on — a resume keeps
/// its model, so the re-prompt records this rather than re-resolving the chain (log #33).
/// <para>
/// SessionId is null when the stream records a pass result whose dispatch this aggregate
/// never saw. A verdict-less pass in that state has no resume target, so the engine fails
/// the run rather than resuming a session it cannot name: an unnamed session is a broken
/// stream, not a decision waiting on a human.
/// </para>
/// </summary>
public sealed record ReviewPassResult(
    ReviewLens Lens,
    Guid? SessionId,
    AgentModel Model,
    ReviewVerdict Verdict);
