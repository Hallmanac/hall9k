namespace Hall9k.Domain.Features.Project;

/// <summary>A named verification gate run in the worktree, e.g. ("test", "dotnet test").</summary>
public sealed record VerifyCommand(string Name, string Command)
{
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
    /// </summary>
    public static string Fingerprint(IReadOnlyList<VerifyCommand> gates) =>
        string.Join(
            '|',
            gates.Select(gate => FormattableString.Invariant(
                $"{gate.Name.Length}:{gate.Name}:{gate.Command.Length}:{gate.Command}")));
}
