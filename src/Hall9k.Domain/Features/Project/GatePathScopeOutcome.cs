using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// What a verification pass records for the gates <see cref="GatePathScope"/> skipped (task: a
/// verify gate can be scoped to a path set). Pure, so <c>VerificationRunner</c> only wires it in
/// and the wording and the shape of what lands on the run stay unit-testable without a branch, a
/// commit or a container.
/// </summary>
public static class GatePathScopeOutcome
{
    /// <summary>
    /// A path-skipped gate's own entry: zero duration and passed (a skip is never a failure),
    /// flagged <see cref="GateDuration.PathScopeSkipped"/> so <c>h9k task show</c> can name the
    /// reason. It never made a narrower run than the configured gates would have, so it stays
    /// <see cref="GateDuration.RanFullScope"/> true, unlike a host-coupled skip.
    /// </summary>
    public static GateDuration SkippedDuration(VerifyCommand gate) =>
        new(gate.Name, TimeSpan.Zero, Passed: true, RanFullScope: true, PathScopeSkipped: true);

    /// <summary>
    /// The note a pass records when every configured gate was path-skipped, worded apart from both
    /// the no-gates-configured note and the non-executable skip; null when any gate runs or none is
    /// configured.
    /// </summary>
    public static string? AllSkippedNote(IReadOnlyList<GatePathScope.GateDecision> decisions) =>
        decisions.Count > 0 && decisions.All(decision => !decision.Runs)
            ? "No gate's path set matched this diff, so every gate was skipped."
            : null;
}
