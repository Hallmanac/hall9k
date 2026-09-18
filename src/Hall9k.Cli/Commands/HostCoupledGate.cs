using System.Text.RegularExpressions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The CLI's own mirror of <c>VerificationRunner</c>'s host-coupled-gate command composition and
/// node-wide permit (task: host-coupled tests run in their own gate once per task, never in
/// parallel with another run's copy — PLACEHOLDER-609bd344). Hall9k.Cli cannot reference
/// Hall9k.Daemon (AGENTS.md's own reference graph — Cli references only Domain and Connectors), so
/// <c>ProjectSetCommand</c>'s own set-time validation and <c>TaskVerifyCommand</c>'s own on-demand
/// gate run both need this logic here rather than shared through a project reference — the
/// identical reason <c>TaskVerifyCommand.RunGateAsync</c> already mirrors
/// <c>VerificationRunner.RunGateAsync</c> instead of calling it. Kept in exactly one place inside
/// this assembly, though, so its two callers here do not duplicate it a second time from each
/// other.
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
    /// </summary>
    internal static bool LooksLikeNoTestsExecuted(string output) =>
        NoTestMatchesWarningPattern().IsMatch(output)
        || ExecutedTestTotalPattern().Matches(output) is { Count: > 0 } totals
            && totals.All(match => !int.TryParse(match.Groups["count"].Value, out int count) || count == 0);

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
