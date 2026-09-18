using System.Text.RegularExpressions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The CLI's own mirror of <c>VerificationRunner</c>'s host-coupled-gate command composition and
/// node-wide permit (task: host-coupled tests run in their own gate once per task, never in
/// parallel with another run's copy — PLACEHOLDER-609bd344). Hall9k.Cli cannot reference
/// Hall9k.Daemon (AGENTS.md's own reference graph — Cli references only Domain and Connectors), but
/// that is not, on its own, why this is duplicated rather than shared (independent pre-PR review,
/// cycle 3, both lenses, low: an earlier version of this comment claimed it was, which does not
/// hold up — <c>ComposeGateCommand</c>, <c>ApplyTestFilter</c> and <c>IsDotnetTestGate</c> are pure
/// functions with no dependency on anything <c>Hall9k.Daemon</c>-scoped, and both assemblies
/// already reference <c>Hall9k.Domain</c>, where <c>VerifyCommand</c> and
/// <c>PlatformPaths.Home</c> already live, so all three, plus the permit's lock file name, could
/// move there and be called from both sides with no reference-graph violation at all). They stay
/// mirrored here instead, the identical shape <c>TaskVerifyCommand.RunGateAsync</c> already takes
/// for <c>VerificationRunner.RunGateAsync</c> itself, which genuinely cannot move (it does depend
/// on the daemon's own <c>ILogger</c> and Marten session). Kept in exactly one place inside this
/// assembly, though, so its two callers here do not duplicate it a second time from each other, and
/// pinned against the daemon's own copy by
/// <see cref="Hall9k.Tests.Cli.HostCoupledGateParityTests"/> so the two cannot silently diverge.
/// </summary>
internal static partial class HostCoupledGate
{
    /// <summary>
    /// The file name a host-coupled gate's own cross-process permit locks — the identical fixed
    /// name and path (under <see cref="PlatformPaths.Home"/>) <c>VerificationRunner</c>'s own copy
    /// locks, so a daemon run's host-coupled gate and an operator's own <c>h9k task verify</c> on
    /// the same machine still never run one at the same time as the other.
    /// </summary>
    private const string LockFileName = ".h9k-host-coupled-gate.lock";

    [GeneratedRegex(@"^\s*dotnet\s+test(?!\w)")]
    private static partial Regex DotnetTestGatePattern();

    /// <summary>Mirrors <c>VerificationRunner.IsDotnetTestGate</c>.</summary>
    internal static bool IsDotnetTestGate(string command) => DotnetTestGatePattern().IsMatch(command);

    /// <summary>Mirrors <c>VerificationRunner.ComposeGateCommand</c>.</summary>
    internal static string ComposeGateCommand(VerifyCommand gate) =>
        gate.HostCoupledFilter is { } filter ? ApplyTestFilter(gate.Command, filter) : gate.Command;

    /// <summary>Mirrors <c>VerificationRunner.ApplyTestFilter</c>.</summary>
    internal static string ApplyTestFilter(string command, string filterExpression)
    {
        int invocationEnd = FindDotnetTestInvocationEnd(command);
        string invocation = command[..invocationEnd].TrimEnd();
        string rest = command[invocationEnd..];

        Match match = ExistingTestFilterPattern().Match(invocation);
        string updatedInvocation = match.Success
            ? string.Concat(
                invocation.AsSpan(0, match.Index),
                $"--filter \"({match.Groups["filter"].Value})&({filterExpression})\"",
                invocation.AsSpan(match.Index + match.Length))
            : $"{invocation} --filter \"{filterExpression}\"";

        return rest.Length == 0 ? updatedInvocation : $"{updatedInvocation} {rest}";
    }

    /// <summary>Mirrors <c>VerificationRunner.FindDotnetTestInvocationEnd</c>.</summary>
    private static int FindDotnetTestInvocationEnd(string command)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (inSingleQuote)
            {
                inSingleQuote = c != '\'';
                continue;
            }

            if (inDoubleQuote)
            {
                inDoubleQuote = c != '"';
                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingleQuote = true;
                    break;
                case '"':
                    inDoubleQuote = true;
                    break;
                case ';':
                case '|':
                    return i;
                case '&' when i == 0 || command[i - 1] != '>':
                    return i;
            }
        }

        return command.Length;
    }

    [GeneratedRegex(
        """--filter(?:\s+|=|:)"(?<filter>[^"]*)"|--filter(?:\s+|=|:)'(?<filter>[^']*)'|--filter(?:\s+|=|:)(?<filter>\S+)""")]
    private static partial Regex ExistingTestFilterPattern();

    [GeneratedRegex("""Total:\s*(?<count>\d+)""")]
    private static partial Regex ExecutedTestTotalPattern();

    [GeneratedRegex("(?i)no test matches the given testcase filter")]
    private static partial Regex NoTestMatchesWarningPattern();

    /// <summary>
    /// Best-effort positive evidence that a gate's own output shows zero tests actually ran —
    /// VSTest's own "no test matches the given testcase filter" warning, or an explicit `Total: 0`
    /// line. Deliberately conservative (independent pre-PR review, cycle 1, adversarial lens,
    /// medium: a host-coupled gate's filter that matches nothing exits 0 under VSTest's default
    /// `TreatNoTestsAsError=false` and would otherwise pass forever as a silent green): unlike
    /// <c>VerificationRunner.ScopedRunExecutedNoTests</c>, this never treats the ABSENCE of a
    /// `Total:` line as evidence on its own, since the caller here only ever has a bounded output
    /// tail to look at (<c>AdHocGateRunner</c> does not capture full output for a passing run), and
    /// a verbose gate's own real summary can sit outside it — a false negative (missing a genuine
    /// vacuous filter) is the safe direction for a best-effort warning to be wrong in, a false
    /// positive is not.
    /// <para>
    /// Once any `Total:` line exists, it — not the warning — decides: the warning is emitted once
    /// per SOURCE, so a multi-project solution where the scoped filter matched in one project and
    /// missed another prints it right alongside a genuine, nonzero `Total:` line from the project
    /// that actually ran (the identical multi-project shape
    /// <c>VerificationRunner.ScopedRunExecutedNoTests</c>'s own doc names as a cycle-3 fix; this
    /// method never received it — independent pre-PR review, cycle 3, adversarial lens, low). Only
    /// with no `Total:` line seen at all does the warning alone count. A `Total:` line whose own
    /// count cannot be parsed (`\d+` matches any run of decimal digits, including one too wide for
    /// <c>int</c>) is treated as a genuine, unreadable count — not confirmed zero, so it must not
    /// count toward "every `Total:` line read zero" the way `!int.TryParse(...) || count == 0`
    /// used to (same review pass, same finding): an unparseable count satisfying the "looks like
    /// zero" side was the identical false-positive direction this method's own doc says it must
    /// never take.
    /// </para>
    /// </summary>
    internal static bool LooksLikeNoTestsExecuted(string output)
    {
        MatchCollection totals = ExecutedTestTotalPattern().Matches(output);
        return totals.Count > 0
            ? totals.All(match => int.TryParse(match.Groups["count"].Value, out int count) && count == 0)
            : NoTestMatchesWarningPattern().IsMatch(output);
    }

    /// <summary>
    /// Acquires the node-wide host-coupled-gate permit, waiting (polling every 200ms) when another
    /// run's own host-coupled gate already holds it. <c>h9k task verify</c> has no run-stream
    /// phase to record a wait against the way the daemon's own copy does — an operator watching
    /// this command's own console output already sees the message its own caller prints before
    /// awaiting this.
    /// </summary>
    internal static async Task<IAsyncDisposable> AcquirePermitAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PlatformPaths.Home);
        string lockFilePath = Path.Combine(PlatformPaths.Home, LockFileName);

        while (true)
        {
            FileStream? stream = TryOpenLockFile(lockFilePath);
            if (stream is not null)
            {
                return new Permit(stream);
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    private static FileStream? TryOpenLockFile(string lockFilePath)
    {
        try
        {
            return new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The permit's own disposable — releasing the file's exclusive lock is the whole release.</summary>
    private sealed class Permit(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
