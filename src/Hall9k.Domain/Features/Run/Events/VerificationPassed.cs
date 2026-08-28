namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// <paramref name="RanFullScope"/> and <paramref name="HeadSha"/> (task: a fix cycle's
/// verification gate) let a later gate decision recognize "this exact tree already had a full
/// run" without re-deriving it: false and null, respectively, on any stream written before these
/// fields existed, which is the conservative default — an unknown gate is never assumed to have
/// already covered a tip, so <c>ReviewEngine</c>'s own skip only ever fires on a stream that
/// actually recorded a full pass over the head in question.
/// </summary>
public sealed record VerificationPassed(
    Guid Id,
    DateTimeOffset PassedAt,
    string? Note = null,
    bool RanFullScope = false,
    string? HeadSha = null);
