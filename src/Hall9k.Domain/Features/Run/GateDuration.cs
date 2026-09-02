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
/// </summary>
public sealed record GateDuration(string Gate, TimeSpan Duration, bool Passed);
