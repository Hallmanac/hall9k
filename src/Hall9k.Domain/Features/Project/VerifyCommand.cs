using System.Diagnostics.CodeAnalysis;

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
/// <see cref="PathSet"/> is null for a gate that runs on every verification pass; non-null scopes
/// the gate to the paths a run changed (task: a verify gate can be scoped to a path set, so a diff
/// that touches none of them records the gate as skipped, TaylorDenny, arx-platform #2218) — set
/// by <c>h9k project set --verify-gate-paths</c>, decided by <see cref="GatePathScope"/>.
/// </summary>
public sealed record VerifyCommand(
    string Name, string Command, string? HostCoupledFilter = null, IReadOnlyList<string>? PathSet = null)
{
    /// <summary>
    /// Whether this gate carries a path set. An empty list reads as none, in equality and in the
    /// fingerprint alike, because a gate scoped to no glob at all would skip on every run, and a
    /// list a hand-edited stream might carry is never allowed to mean that.
    /// </summary>
    [MemberNotNullWhen(true, nameof(PathSet))]
    public bool IsPathScoped => PathSet is { Count: > 0 };

    /// <summary>Whether this gate is the project's host-coupled one (<see cref="HostCoupledFilter"/> non-null).</summary>
    public bool IsHostCoupled => HostCoupledFilter is not null;

    /// <summary>
    /// Gate equality compares <see cref="PathSet"/> by contents, in order (the positional record's
    /// own member-wise equality would compare the list by reference once it is deserialized):
    /// <c>h9k project set</c> re-validates only the gates whose configuration changed, and
    /// <c>GateSetAcceptance.Diff</c> shows an unchanged gate as unchanged, both through this.
    /// </summary>
    public bool Equals(VerifyCommand? other) =>
        other is not null
        && Name == other.Name
        && Command == other.Command
        && HostCoupledFilter == other.HostCoupledFilter
        && (IsPathScoped
            ? other.IsPathScoped && PathSet.SequenceEqual(other.PathSet)
            : !other.IsPathScoped);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name);
        hash.Add(Command);
        hash.Add(HostCoupledFilter);
        if (IsPathScoped)
        {
            foreach (string glob in PathSet)
            {
                hash.Add(glob);
            }
        }

        return hash.ToHashCode();
    }

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
    /// <see cref="PathSet"/> feeds it only for a gate that has one, appended after the filter as a
    /// count and then each glob length-prefixed the same way, so a gate without a path set
    /// fingerprints exactly as it did before the field existed (a gate set an operator accepted
    /// earlier stays accepted) and <c>["a,b"]</c> never collides with <c>["a","b"]</c>.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<VerifyCommand> gates) =>
        string.Join(
            '|',
            gates.Select(gate => FormattableString.Invariant(
                $"{gate.Name.Length}:{gate.Name}:{gate.Command.Length}:{gate.Command}:{gate.HostCoupledFilter?.Length ?? -1}:{gate.HostCoupledFilter}{PathSetFingerprint(gate)}")));

    private static string PathSetFingerprint(VerifyCommand gate) =>
        gate.IsPathScoped
            ? FormattableString.Invariant($":{gate.PathSet.Count}")
                + string.Concat(gate.PathSet.Select(glob => FormattableString.Invariant($":{glob.Length}:{glob}")))
            : "";
}
