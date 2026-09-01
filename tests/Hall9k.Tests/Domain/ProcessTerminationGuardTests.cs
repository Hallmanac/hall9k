using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Process exit is owned exclusively by the two entry points, <c>Hall9k.Cli/Program.cs</c> and
/// <c>Hall9k.Daemon/Program.cs</c> — every other line of production code reports an outcome
/// through a return value and lets its caller decide what happens next, all the way up to
/// whichever <c>Program.cs</c> is actually running. A command internal, a connector, or a domain
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
/// or in the history at the time of the incident, and the mechanism that actually explains it —
/// documented in the PR this guard shipped with — was the then-unbounded Testcontainers Postgres
/// concurrency PLAN.md decision #108 fixed two days later, which OOM-kills the test host in a way
/// that presents identically at the VSTest level. This guard exists so the hypothesis this
/// investigation cleared stays cleared: a future process-terminating call reachable from a test
/// fails the build here instead of ever reaching a human as an unexplained crash again. This is a
/// source scan rather than a runtime check for the same reason <see cref="ContainerRoutingGuardTests"/>
/// is: the failure mode is "the call exists at all", which by construction this process can never
/// intercept by actually exercising it — the whole point is that nothing may ever call it during a
/// test run to find out.
/// <para>
/// The scan matches against comment/string-stripped source
/// (<see cref="TestSourceTree.StripCommentsAndStrings"/>), not the raw text, so a file that only
/// names <c>Environment.Exit</c>/<c>Environment.FailFast</c> in a doc comment — this file's own
/// doc comment above among them — is never mistaken for a real call.
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
/// The marker list only names the two literal spellings <c>Environment.Exit</c> and
/// <c>Environment.FailFast</c>, so it does not catch an equivalent termination reached another
/// way — <c>Process.GetCurrentProcess().Kill()</c>, or a <c>using static System.Environment;</c>
/// with a bare <c>Exit(1)</c> — the same class of blind spot <see cref="ContainerRoutingGuardTests"/>
/// documents for its own scan. This guard cannot follow a termination there.
/// </para>
/// </summary>
public sealed class ProcessTerminationGuardTests
{
    private static readonly string[] TerminationMarkers =
    [
        "Environment.Exit",
        "Environment.FailFast",
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
        Path.Combine("Hall9k.Cli", "Program.cs"),
        Path.Combine("Hall9k.Daemon", "Program.cs"),
    ];

    [Fact]
    public void No_production_code_outside_program_cs_terminates_the_process()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();

        string[] files =
        [
            .. Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !ExemptRelativePaths.Contains(Path.GetRelativePath(sourceDirectory, file)))
               .Where(file => !TestSourceTree.IsBuildOutput(sourceDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));
            string relativePath = Path.GetRelativePath(sourceDirectory, file);

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
            100,
            "this is far fewer .cs files than src/ actually holds — TestSourceTree.SourceDirectory() is " +
            "probably no longer resolving to the repository's src/ directory");

        // A positive control on the scan itself, the same reason ContainerRoutingGuardTests keeps
        // one: with no real offending call left in src/ for the marker list to find (that is the
        // point of the guard), a scan that has gone dark — StripCommentsAndStrings regressed into
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

        (string strippedOffending, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticOffendingCode);
        (string strippedQualified, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticQualifiedCode);
        (string strippedComment, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticInnocentComment);
        (string strippedLookAlike, _, _) = TestSourceTree.StripCommentsAndStrings(syntheticLookAlikeCode);

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
    }
}
