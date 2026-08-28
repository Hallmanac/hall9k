namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// <paramref name="RanFullScope"/> and <paramref name="HeadSha"/> (task: a fix cycle's
/// verification gate) let a later gate decision recognize "this exact tree already had a full
/// run" without re-deriving it: false and null, respectively, on any stream written before these
/// fields existed, which is the conservative default — an unknown gate is never assumed to have
/// already covered a tip, so <c>ReviewEngine</c>'s own skip only ever fires on a stream that
/// actually recorded a full pass over the head in question. <paramref name="VerifyCommandsFingerprint"/>
/// (Copilot review, PR #62) closes the companion gap: a HEAD match alone cannot tell "the same
/// gates ran" from "a human changed the project's verify commands between this pass and now", so
/// <c>ReviewEngine</c>'s skip also requires the project's CURRENT <see
/// cref="Hall9k.Domain.Features.Project.VerifyCommand.Fingerprint"/> to match this recorded one.
/// Null on any stream written before this field existed — unlike <paramref name="RanFullScope"/>
/// and <paramref name="HeadSha"/>, that unknown is read as a match rather than a mismatch
/// (independent pre-PR review, cycle 3, adversarial lens): the fingerprint question is moot when it
/// was never recorded in the first place, so it defers to whatever <paramref name="RanFullScope"/>
/// and <paramref name="HeadSha"/> already decide, rather than an unobserved field masquerading as an
/// observed change and forcing a redundant gate — or, worse, a whole redundant review round — on a
/// stream that predates this field.
/// </summary>
public sealed record VerificationPassed(
    Guid Id,
    DateTimeOffset PassedAt,
    string? Note = null,
    bool RanFullScope = false,
    string? HeadSha = null,
    string? VerifyCommandsFingerprint = null);
