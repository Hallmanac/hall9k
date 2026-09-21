namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Recorded instead of <see cref="VerificationPassed"/> when <c>VerificationRunner</c> classified
/// every path this run's branch changed against its base as matching the project's
/// non-executable-path set, before any build or test gate ran (task: a delivered diff that
/// touches no buildable or testable source skips the build and test gates). <paramref
/// name="ChangedPaths"/> names every changed path and the rule it matched, in diff order —
/// deletions and renames included, both sides of a rename recorded separately so either one
/// falling outside the set is what forces the ordinary gates to run instead.
/// <para>
/// A skip is never a pass for scoping: unlike <see cref="VerificationPassed"/>,
/// <c>RunAggregate.Apply(VerificationSkipped)</c> deliberately leaves <c>LastGateRanFullScope</c>,
/// <c>LastGateHeadSha</c>, and <c>LastGateVerifyCommandsFingerprint</c> untouched, so the next gate
/// that actually runs always scopes off the last real <see cref="VerificationPassed"/> on the
/// stream, never off this fact — and this fact can never satisfy
/// <c>ReviewEngine</c>'s "already ran full over this HEAD" waiver either, for the identical reason.
/// </para>
/// </summary>
public sealed record VerificationSkipped(
    Guid Id,
    DateTimeOffset SkippedAt,
    IReadOnlyList<VerificationSkippedPath> ChangedPaths);
