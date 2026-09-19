namespace Hall9k.Domain.Features.Project;

/// <summary>
/// A named verification gate run in the worktree, e.g. ("test", "dotnet test").
/// <see cref="HostCoupledFilter"/> is null for an ordinary gate; non-null marks the gate
/// host-coupled and carries the `dotnet test --filter` expression <c>VerificationRunner</c>
/// injects into <see cref="Command"/> when it actually runs it (task: host-coupled tests run in
/// their own gate once per task, never in parallel with another run's copy). A host-coupled gate
/// runs only at a run's first verification and its final full pass, is serialized against every
/// other host-coupled gate on the same node, and is skipped on every intermediate review-cycle
/// pass — set by <c>h9k project set --verify-gate-filter</c>.
/// </summary>
public sealed record VerifyCommand(string Name, string Command, string? HostCoupledFilter = null)
{
    /// <summary>Whether this gate is the project's host-coupled one (<see cref="HostCoupledFilter"/> non-null).</summary>
    public bool IsHostCoupled => HostCoupledFilter is not null;

    /// <summary>
    /// A stable identity for a whole project's gate configuration (task: a fix cycle's
    /// verification gate) — what lets a later gate decision tell "these are still the gates
    /// that ran" from "a human changed verify settings mid-run" without re-running anything to
    /// find out. Order-sensitive: gates run in the recorded order, and a reorder is as real a
    /// configuration change as a different command string. Each gate is length-prefixed rather
    /// than joined with a bare delimiter (independent pre-PR review, cycle 1 — both a name and a
    /// command are free text that `ProjectSetCommand.ParseVerify` splits only on the first `=`,
    /// so either may itself contain `:` or `|`; a bare join let two different gate configurations,
    /// e.g. two gates `("build","dotnet build")`/`("test","dotnet test")` and the single gate
    /// `("build","dotnet build|test:dotnet test")`, collide on the identical fingerprint string).
    /// The length prefixes are formatted with the invariant culture (Copilot review, PR #86 —
    /// this fingerprint is compared across nodes/processes, and the default interpolation
    /// formats an int against <see cref="System.Globalization.CultureInfo.CurrentCulture"/>,
    /// which renders non-ASCII digit shapes under some cultures and would make an identical gate
    /// configuration fingerprint differently on a node running under one of them).
    /// <see cref="HostCoupledFilter"/> feeds the same fingerprint (#225): which
    /// gate is host-coupled, and what it is filtered to, changes what a pass actually covers just
    /// as much as a changed <see cref="Command"/> does.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<VerifyCommand> gates) =>
        string.Join(
            '|',
            gates.Select(gate => FormattableString.Invariant(
                $"{gate.Name.Length}:{gate.Name}:{gate.Command.Length}:{gate.Command}:{gate.HostCoupledFilter?.Length ?? -1}:{gate.HostCoupledFilter}")));
}
