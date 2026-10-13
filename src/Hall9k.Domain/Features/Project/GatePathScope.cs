namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Decides, for one verification pass, which of a project's gates run and which skip out because
/// their <see cref="VerifyCommand.PathSet"/> names none of the paths the run changed (task: a
/// verify gate can be scoped to a path set, a teammate's project — a one-line comment in
/// a front-end file paid about 30 minutes of .NET gates that could not observe it). A pure
/// function over a path list, the non-executable rule set and the gates, in the same spirit as
/// <see cref="NonExecutablePathClassifier"/>: nothing here reads git or the filesystem, so
/// <c>VerificationRunner</c> alone decides which paths this sees.
/// </summary>
public static class GatePathScope
{
    /// <summary>One gate and whether this pass runs it.</summary>
    public readonly record struct GateDecision(VerifyCommand Gate, bool Runs);

    /// <summary>
    /// Every gate, in the recorded order, with its run-or-skip answer. A gate with no path set
    /// always runs. When <paramref name="changedPaths"/> is null (the diff could not be read) or
    /// empty, every gate runs whatever its path set: a skip is never guessed from a fact nobody
    /// observed (AGENTS.md's "unobserved is unknown" rule). Otherwise the paths matching
    /// <paramref name="nonExecutableRules"/> are dropped first, since a documentation edit says
    /// nothing about whether a front-end or backend gate has anything to observe, and a scoped
    /// gate runs when at least one remaining path matches its set. A set is matched with
    /// <see cref="NonExecutablePathClassifier"/>'s own rules, so a leading <c>!</c> exclusion wins
    /// over every positive glob in the same set.
    /// </summary>
    public static IReadOnlyList<GateDecision> Decide(
        IReadOnlyList<string>? changedPaths,
        IReadOnlyList<string> nonExecutableRules,
        IReadOnlyList<VerifyCommand> gates)
    {
        if (changedPaths is not { Count: > 0 })
        {
            return [.. gates.Select(gate => new GateDecision(gate, Runs: true))];
        }

        IReadOnlyList<string> executablePaths =
            [.. changedPaths.Where(path => NonExecutablePathClassifier.MatchRule(path, nonExecutableRules) is null)];
        return
        [
            .. gates.Select(gate => new GateDecision(
                gate,
                Runs: !gate.IsPathScoped
                    || executablePaths.Any(path => NonExecutablePathClassifier.MatchRule(path, gate.PathSet) is not null))),
        ];
    }

    /// <summary>
    /// Whether a path set can ever match anything: it needs at least one positive glob, because a
    /// set of only <c>!</c> exclusions would skip its gate on every run.
    /// </summary>
    public static bool HasPositiveGlob(IReadOnlyList<string> pathSet) =>
        pathSet.Any(glob => glob.Length > 0 && !glob.StartsWith('!'));

    /// <summary>
    /// Whether a glob (a leading <c>!</c> aside) is written in a shape no path git reports can
    /// match: <see cref="NonExecutablePathClassifier"/> normalizes the changed path, never the
    /// rule, and git prints repository-relative paths with forward slashes and no leading
    /// <c>/</c> or <c>./</c>. A gate scoped to one such glob would skip on every run, the same loss
    /// of verification <see cref="HasPositiveGlob"/> guards against.
    /// </summary>
    public static bool CanNeverMatch(string glob)
    {
        string body = glob.StartsWith('!') ? glob[1..] : glob;
        return body.StartsWith('/') || body.StartsWith("./", StringComparison.Ordinal) || body.Contains('\\');
    }
}
