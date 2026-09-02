namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One gate's own wall-clock duration from a single verification pass (task: gate wall-clock
/// duration is recorded and surfaced — origin incident 2026-09-01, the full suite roughly
/// doubling in a week and going unnoticed for three days, reconstructed only after the fact
/// from verify-build.log to verify-test.log file-modification-time gaps). Measured around the
/// gate's own execution the runner already performs — nothing else <c>VerifyAsync</c> does
/// before or after it. When a gate spends its one infrastructure-classified retry
/// (<see cref="Events.GateRetried"/>) before resolving, <see cref="Duration"/> is the sum of
/// both attempts rather than two separate entries: the wall clock the gate actually cost this
/// pass, whichever attempt is what <see cref="Passed"/> describes.
/// <see cref="RanFullScope"/> is this one gate's own scope, not the whole pass's: a fix cycle's
/// reverify can narrow one `dotnet test`-shaped gate while a sibling gate in the same pass falls
/// back to full or was never scopable at all (<c>VerificationRunner</c>'s own per-gate fallback
/// accounting), so a pass-level flag would wrongly tag every gate in it alike. True for a gate
/// scope never touches (a build or lint gate, or a test gate that ran unscoped or fell back to
/// full), false only for a `dotnet test`-shaped gate that actually ran narrowed — the distinction
/// <see cref="Queries.GateDurationHistoryQuery"/> needs so a scoped fix-cycle sample never gets
/// averaged in against a full-scope one. Defaults true on a stream written before this field
/// existed, the same "assume comparable to full scope" default the no-gates-configured pass
/// already records for its own <c>RanFullScope</c>.
/// </summary>
public sealed record GateDuration(string Gate, TimeSpan Duration, bool Passed, bool RanFullScope = true);
