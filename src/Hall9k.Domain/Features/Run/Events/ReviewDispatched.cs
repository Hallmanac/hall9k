using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One independent review pass was spawned over the run's diff before its pull request
/// opens (Decisions Log #23): a separate headless session with fresh context — never the
/// session that wrote the code. Cycle counts review rounds from 1. A
/// <see cref="ReviewMode.Discovery"/> or <see cref="ReviewMode.FinalFullPass"/> cycle dispatches
/// one of these per lens (log #59); a <see cref="ReviewMode.Verify"/> cycle dispatches exactly one,
/// recorded under <see cref="ReviewLens.Verify"/>, standing in for every still-active track (task:
/// review cycles after the first). ProcessId + start time are the session's identity for adoption
/// (the PID-reuse guard, log #2). Model is the resolved model this pass was spawned on, recorded
/// per pass as an observed fact (log #33). Lens is which attention budget this pass carries; null
/// on streams written before lenses existed. Mode is which shape this cycle's dispatch took; null
/// reads as <see cref="ReviewMode.Discovery"/>, the only shape a stream written before this field
/// existed could have carried. HeadSha is the worktree's `git rev-parse HEAD` at the moment this
/// pass was spawned, best-effort (null when it could not be read) — what a later Verify cycle's
/// prompt points a "commits since the prior cycle" instruction at. SinceSha is the boundary this
/// pass's own diff instruction was actually scoped to when it dispatched — null for a
/// <see cref="ReviewMode.Discovery"/> pass (always a full base-branch read) and for a
/// <see cref="ReviewMode.FinalFullPass"/> pass with no earlier full-scope boundary on record (also
/// a full read); the prior cycle's own tip for a <see cref="ReviewMode.Verify"/> pass, the boundary
/// its delta read is scoped since — but null there too whenever that prior cycle's own HeadSha
/// could not be read, since the boundary is only ever as good as that best-effort read (the Verify
/// prompt falls back to a full-range instruction rather than inventing one); and the run's last
/// full-scope read for a <see cref="ReviewMode.FinalFullPass"/> pass that was itself scoped to the
/// commits since it. Null therefore never rules a mode out: it says this pass read the full range,
/// whichever mode asked for it. Recorded as an observed fact because only the dispatch that
/// resolved it ever knows for certain, the same reasoning
/// <see cref="VerificationPassed.RanFullScope"/> already follows — what lets a later
/// <see cref="ReviewMode.Verify"/> pass's prompt (<c>AgentPromptBuilder.BuildReviewVerify</c>) say
/// honestly whether the cycle it is quoting findings from read the branch in full or only a delta.
/// RunState → UnderReview.
/// </summary>
public sealed record ReviewDispatched(
    Guid Id,
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt,
    AgentModel? Model = null,
    ReviewLens? Lens = null,
    ReviewMode? Mode = null,
    string? HeadSha = null,
    string? SinceSha = null);
