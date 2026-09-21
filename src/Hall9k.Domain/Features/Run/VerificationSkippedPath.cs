namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One path <see cref="Events.VerificationSkipped"/> observed changed, and the project's
/// non-executable-path rule it matched (task: a delivered diff that touches no buildable or
/// testable source skips the build and test gates) — never recorded without a match, since the
/// event itself is only ever appended once every changed path matched some rule.
/// </summary>
public sealed record VerificationSkippedPath(string Path, string MatchedRule);
