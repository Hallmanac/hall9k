using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Process exit is owned exclusively by the two exempted entry points, <c>Hall9k.Cli/Program.cs</c>
/// and <c>Hall9k.Daemon/Program.cs</c> — every other line of production code reports an outcome
/// through a return value and lets its caller decide what happens next, all the way up to
/// whichever <c>Program.cs</c> is actually running. <c>src/</c> holds a third top-level-statement
/// entry point, <c>Hall9k.AppHost/AppHost.cs</c>, and it is deliberately not exempted: it
/// orchestrates the local Aspire dev loop only, is never the process a dispatched agent or a
/// production install runs, and has no call reachable from a test today (this guard's scan
/// confirms it, the same as everywhere else under <c>src/</c>). If AppHost ever needs a real exit
/// call, add its path to <see cref="ExemptRelativePaths"/> explicitly rather than assuming it is
/// already covered by "the two" above. A command internal, a connector, or a domain
/// handler that instead called <see cref="Environment.Exit(int)"/> or
/// <see cref="Environment.FailFast(string?)"/> directly would tear down the whole process it
/// happens to be hosted in — including, for any such call reachable from a test (this project has
/// plenty of command internals under direct, in-process test coverage, <c>UpdateCommand.RunAsync</c>
/// among them), the <c>dotnet test</c> host itself, which is indistinguishable from an unrelated
/// crash: xUnit has no chance to report the offending test as failed, every other test still in
/// flight is torn down mid-run, and the suite's own summary line never gets written. This guard
/// was added investigating an origin incident (2026-08-29: a full 2295/2295 green run reported
/// "Test Run Aborted. Reason: Test host process crashed" instead of a passing summary) as one of
/// two hypotheses for the crash; the investigation found no such call anywhere in this tree, live
/// or in the history at the time of the incident. PLAN.md decision #108's then-unbounded
/// Testcontainers Postgres concurrency, fixed two days later, is the remaining explanation the
/// timeline supports once both cleared hypotheses are eliminated — not itself directly confirmed
/// for that specific abort (decisions #109, #110 record what was and was not checked). This guard
/// exists so the hypothesis this investigation cleared stays cleared: a future process-terminating
/// call reachable from a test via production code under <c>src/</c> fails the build here instead
/// of ever reaching a human as an unexplained crash again. A call added directly inside the test
/// tree itself — a fake, a fixture, a helper under <c>tests/</c> — is equally reachable from a
/// test, and decision #110 closed that scope gap: the scan below now covers <c>tests/</c> too,
/// this file's own doc comment included. This is a source scan rather than a runtime check for the
/// same reason <see cref="ContainerRoutingGuardTests"/> is: the failure mode is "the call exists at
/// all", which by construction this process can never intercept by actually exercising it — the
/// whole point is that nothing may ever call it during a test run to find out.
/// <para>
/// The scan matches against comment/string-stripped source
/// (<see cref="TestSourceTree.StripCommentsAndStrings"/>), not the raw text, so a file that only
/// names <c>Environment.Exit</c>/<c>Environment.FailFast</c> in a doc comment — this one included,
/// now that the scan reads <c>tests/</c> as well as <c>src/</c> — is never mistaken for a real call.
/// </para>
/// <para>
/// The two <c>Program.cs</c> entry points are the sole exemption, by relative path exactly like
/// <see cref="Hall9k.Tests.Integration.PostgresFixture"/> is <see cref="ContainerRoutingGuardTests"/>'s:
/// today neither actually calls <see cref="Environment.Exit(int)"/> (both return an <c>int</c>
/// from their top-level <c>Main</c> and let the runtime map that to the process exit code), so the
/// exemption is currently unexercised — but it is where the architecture intends the one legitimate
/// call to live if either entry point ever needs one, and nowhere else.
/// </para>
/// <para>
/// The marker list also matches <c>GetCurrentProcess().Kill</c>, but it does not actually cover
/// what it was added to cover. Two legitimate call sites
/// (<see cref="Hall9k.Daemon.SingleInstanceGuard"/> and
/// <see cref="Hall9k.Daemon.WindowsStopRequestWatcher"/>) already hold
/// <c>Process.GetCurrentProcess()</c> for benign <c>Id</c>/<c>StartTime</c> reads, but both bind it
/// to a local (<c>using Process current = Process.GetCurrentProcess();</c>) rather than chaining
/// off the expression directly, so a <c>.Kill()</c> added at either site would read
/// <c>current.Kill();</c> — which the literal marker <c>GetCurrentProcess().Kill</c> can never
/// match. The marker still catches a <c>.Kill()</c> chained directly off a fresh
/// <c>Process.GetCurrentProcess()</c> call anywhere else in the scanned tree, but it is not the
/// safety net for these two sites that it looks like; that gap sits alongside the one documented
/// next rather than being closed by it. It does not catch every equivalent, either — a file that
/// adds <c>using static System.Environment;</c> and calls a bare <c>Exit(1)</c> reaches the same
/// termination through an unqualified name this scan cannot distinguish from an ordinary method
/// call, the same class of blind spot <see cref="ContainerRoutingGuardTests"/> documents for its
/// own scan. This guard cannot follow a termination there.
/// </para>
/// </summary>
public sealed class ProcessTerminationGuardTests
{
    private static readonly string[] TerminationMarkers =
    [
        "Environment.Exit",
        "Environment.FailFast",
        "GetCurrentProcess().Kill",
    ];

    /// <summary>
    /// True when <paramref name="marker"/> appears in <paramref name="code"/> as its own call
    /// rather than as part of a longer identifier — so the <c>"Environment.Exit"</c> marker
    /// matches <c>Environment.Exit(1)</c> but neither <c>Environment.ExitCode</c>, which sets a
    /// value and terminates nothing, nor a same-suffixed unrelated type's member such as
    /// <c>FakeEnvironment.Exit(…)</c> or <c>HostEnvironment.ExitReason</c>.
    /// <para>
    /// A boundary is checked on both sides, but the two sides do not use the same rule: a
    /// preceding <c>.</c> is deliberately a boundary, so a fully qualified
    /// <c>System.Environment.Exit(1)</c> still matches, while a preceding letter, digit or
    /// underscore is not, which is what rules out the longer-type-name shapes above. Treating a
    /// preceding <c>.</c> as part of the identifier instead would silently drop the qualified
    /// spelling — a real termination the guard exists to catch — so the asymmetry is the point
    /// rather than an oversight, and the positive control below asserts both halves of it.
    /// </para>
    /// </summary>
    private static bool ContainsMarkerAsIdentifier(string code, string marker)
    {
        int index = 0;
        while ((index = code.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            int after = index + marker.Length;
            bool endsAtBoundary = after >= code.Length
                || !(char.IsLetterOrDigit(code[after]) || code[after] == '_');
            bool startsAtBoundary = index == 0
                || !(char.IsLetterOrDigit(code[index - 1]) || code[index - 1] == '_');

            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static readonly string[] ExemptRelativePaths =
    [
        Path.Combine("src", "Hall9k.Cli", "Program.cs"),
        Path.Combine("src", "Hall9k.Daemon", "Program.cs"),
    ];

    [Fact]
    public void No_production_code_outside_program_cs_terminates_the_process()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();
        string testsDirectory = TestSourceTree.RootDirectory();

        // repositoryRoot is sourceDirectory's own parent (SourceDirectory() resolves to
        // "<repositoryRoot>/src"), which testsDirectory also sits under
        // ("<repositoryRoot>/tests/Hall9k.Tests") — a single common base lets every offender and
        // every exemption below be reported by one repository-relative path regardless of which
        // of the two trees it came from, rather than a src/-relative path that reads wrong for a
        // tests/ file.
        string repositoryRoot = Path.GetDirectoryName(sourceDirectory)
            ?? throw new InvalidOperationException($"'{sourceDirectory}' has no parent directory");

        // Decision #110: a process-terminating call added directly inside the test tree — a fake,
        // a fixture, a helper — is exactly as reachable from a test as one added to production
        // code, so both trees are scanned here rather than src/ alone.
        string[] files =
        [
            .. Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !TestSourceTree.IsBuildOutput(sourceDirectory, file)),
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, file);
            if (ExemptRelativePaths.Contains(relativePath))
            {
                continue;
            }

            (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));

            if (!balanced)
            {
                // Mirrors ContainerRoutingGuardTests' own handling of the same signal: a file
                // StripCommentsAndStrings desyncs on cannot be trusted to have surfaced every real
                // Environment.Exit/Environment.FailFast call it contains, so it is reported as an
                // offender itself rather than silently dropped from coverage.
                offenders.Add(
                    $"{relativePath} <StripCommentsAndStrings desynced on this file: stripped brace " +
                    "depth never returned to zero, so its process-termination coverage cannot be trusted>");
                continue;
            }

            if (TerminationMarkers.Any(marker => ContainsMarkerAsIdentifier(code, marker)))
            {
                offenders.Add(relativePath);
            }
        }

        offenders.Should().BeEmpty(
            "process exit belongs exclusively to Hall9k.Cli/Program.cs and Hall9k.Daemon/Program.cs — " +
            "every other line of production code reports an outcome through a return value instead " +
            "of calling Environment.Exit/Environment.FailFast directly, because a call reachable " +
            "from a unit test tears down the dotnet test host itself rather than failing the one " +
            "test that reached it (origin incident, 2026-08-29: a full green run aborted with " +
            "\"Test host process crashed\" instead of reporting its actual summary)");

        files.Length.Should().BeGreaterThan(
            500,
            "this is far fewer .cs files than src/ and tests/ together actually hold — " +
            "TestSourceTree.SourceDirectory()/RootDirectory() are probably no longer resolving to " +
            "the repository's src/ and tests/Hall9k.Tests directories");

        // A positive control on the scan itself, the same reason ContainerRoutingGuardTests keeps
        // one: with no real offending call left in src/ or tests/ for the marker list to find
        // (that is the point of the guard), a scan that has gone dark — StripCommentsAndStrings
        // regressed into
        // over-stripping, or ContainsMarkerAsIdentifier stopped recognizing its own marker text —
        // would report success while checking nothing, with no failing assertion above to say so.
        // Built from a synthetic snippet rather than pointing at a real file, since (unlike
        // ContainerRoutingGuardTests' allowed construction) there is no legitimate call anywhere
        // in this tree to point at — which means, unlike that guard's control, this one is built
        // from the same TerminationMarkers text it checks, so it cannot catch the marker list
        // itself having drifted from how the real API is spelled (e.g. a hypothetical rename of
        // Environment.Exit); it only catches a stripping or matching regression.
        const string syntheticOffendingCode = "if (failure) { Environment.Exit(1); }";
        const string syntheticQualifiedCode = "if (failure) { System.Environment.Exit(1); }";
        const string syntheticInnocentComment = "// Environment.FailFast(\"never actually called\")";
        const string syntheticLookAlikeCode = "if (failure) { FakeEnvironment.Exit(1); }";
        const string syntheticKillCode = "if (failure) { Process.GetCurrentProcess().Kill(); }";
        // Environment.ExitCode sets a value and terminates nothing (the doc comment on
        // ContainsMarkerAsIdentifier promises it is not mistaken for Environment.Exit), but
        // nothing below exercised that: dropping or inverting the trailing-boundary check would
        // still pass every other control here while flagging this line as an offender.
        const string syntheticExitCodeAssignment = "Environment.ExitCode = 1;";

        (string strippedOffending, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticOffendingCode);
        (string strippedQualified, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticQualifiedCode);
        (string strippedComment, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticInnocentComment);
        (string strippedLookAlike, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticLookAlikeCode);
        (string strippedKill, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticKillCode);
        (string strippedExitCodeAssignment, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticExitCodeAssignment);

        // Each name says what the scan actually reports for that snippet, not what the assertion
        // below wants it to be — a boolean named for the desired verdict rather than the observed
        // one reads as inverted against its own assertion, and invites a later reader to "correct"
        // the assertion into passing only when the scan is broken.
        bool scanSeesARealCall = TerminationMarkers.Any(
            marker => ContainsMarkerAsIdentifier(strippedOffending, marker));
        bool scanSeesAQualifiedCall = TerminationMarkers.Any(
            marker => ContainsMarkerAsIdentifier(strippedQualified, marker));
        bool scanSeesACommentedMention = TerminationMarkers.Any(
            marker => ContainsMarkerAsIdentifier(strippedComment, marker));
        bool scanSeesALookAlikeMember = TerminationMarkers.Any(
            marker => ContainsMarkerAsIdentifier(strippedLookAlike, marker));
        bool scanSeesAProcessKill = TerminationMarkers.Any(
            marker => ContainsMarkerAsIdentifier(strippedKill, marker));
        bool scanSeesAnExitCodeAssignment = TerminationMarkers.Any(
            marker => ContainsMarkerAsIdentifier(strippedExitCodeAssignment, marker));

        scanSeesARealCall.Should().BeTrue(
            "the marker list no longer matches a real Environment.Exit call, or comment/string " +
            "stripping is eating real code — this guard would report success while protecting nothing");
        scanSeesAQualifiedCall.Should().BeTrue(
            "a fully qualified System.Environment.Exit(1) terminates the process exactly as the " +
            "unqualified spelling does, so the leading-boundary check must treat the preceding " +
            "'.' as a boundary rather than as part of the identifier");
        scanSeesACommentedMention.Should().BeFalse(
            "a call name that only appears inside a comment is being treated as a real one — this " +
            "guard would conscript any file that merely discusses Environment.FailFast in prose");
        scanSeesALookAlikeMember.Should().BeFalse(
            "a member of an unrelated type whose name merely ends in the marker text — a test shim " +
            "named FakeEnvironment, say — terminates nothing, so the leading-boundary check must " +
            "rule it out rather than failing the build on a file that is not an offender");
        scanSeesAProcessKill.Should().BeTrue(
            "Process.GetCurrentProcess().Kill() terminates the process exactly as Environment.Exit " +
            "does, so this marker must not have gone dark — though it does not, on its own, cover " +
            "the two legitimate GetCurrentProcess() reads in this tree (SingleInstanceGuard, " +
            "WindowsStopRequestWatcher): both bind the result to a local first, so a .Kill() added " +
            "at either site would read current.Kill(), which this marker's literal text never matches");
        scanSeesAnExitCodeAssignment.Should().BeFalse(
            "Environment.ExitCode sets a value and terminates nothing, so the trailing-boundary " +
            "check must rule it out rather than failing the build on a file that only ever sets an " +
            "exit code, never calls Exit");
    }
}
