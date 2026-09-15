using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The gh-site migration's own enforcement (idea 202383dc, A2b item 4): every <c>gh</c> spawn in
/// the platform funnels through <see cref="Hall9k.Connectors.WorkItems.ProjectGitHubClient"/>, the
/// one place that pins the account the call runs as, rather than a raw <c>System.Diagnostics.Process</c>
/// spawned wherever a connector or command happens to need <c>gh</c>. Two independent scans, both
/// over <c>src/</c> only — test doubles are exempt on both counts, since a fake naming <c>"gh"</c>
/// or constructing a connector bare is exactly what a test is for.
/// <para>
/// <b>No raw spawn.</b> <see cref="Hall9k.Connectors.Processes.ExternalProcess"/> is the sole place
/// a <c>ProcessStartInfo</c> is ever built for <c>gh</c> by name — every other class reaches it only
/// through the <c>ProcessRunner</c>/<c>EnvironmentProcessRunner</c> delegate seam, which is what
/// lets an account be pinned per call. This scan matches the raw text (not comment/string-stripped,
/// since the marker itself is the quoted literal <c>"gh"</c>) for the exact shapes a raw spawn
/// takes in this codebase — <c>FileName = "gh"</c> and <c>new ProcessStartInfo("gh"</c> — which is
/// precisely what the two sites this task removed (<c>GitHubPullRequestInspector.RunGhAsync</c>,
/// <c>PullRequestOpener.CreatePullRequestAsync</c>) used to read.
/// </para>
/// <para>
/// <b>No bare, unaccounted construction.</b> Every GitHub-or-tracker connector class that used to
/// default its own <c>ProcessRunner</c> to the unaccounted <c>ExternalProcess.Runner</c> now gets
/// one explicitly, wired at the one place it is constructed for real: a CLI command's own
/// <c>ExecuteAsync</c>, or the daemon's single <c>ProcessRunner</c> DI registration
/// (<c>ProjectScopedGitHubRunner</c>, Program.cs). A bare, zero-argument construction anywhere
/// outside the two sites named in <see cref="AllowedBareConstructionRelativePaths"/> — both of
/// which only ever call the construction's synchronous, gh-free <c>WebUrl</c> — would silently
/// reintroduce an unaccounted <c>gh</c> call the way the CLI's <c>ReviewResolveCommand</c>,
/// <c>PullRequestApproveCommand</c>, and the rest all used to. Scanned against comment/string-stripped
/// source (<see cref="TestSourceTree.StripCommentsAndStrings"/>), since a doc comment mentioning one
/// of these constructions in prose (<c>CloseoutEngine.cs</c>'s own, for one) is not a real call.
/// </para>
/// <para>
/// Both scans are deliberately narrower than "every gh call chooses an account": that is a wiring
/// property no source scan can fully prove, and is instead verified by
/// <c>ProjectScopedGitHubRunnerTests</c> and <c>ProjectGitHubClientTests</c> actually exercising the
/// resolution. What this guard catches is the specific, mechanical regression class both scans name:
/// a raw spawn reappearing, or a connector construction reverting to the unaccounted default.
/// <c>NodeBootstrap.RunQuick</c> is the one deliberate exception this migration leaves in place —
/// it discovers the very GitHub identity <see cref="Hall9k.Connectors.WorkItems.ProjectGitHubClient.ResolveAccountAsync"/>
/// later reads, so there is no account yet to resolve, and its raw <c>ProcessStartInfo(fileName, arguments)</c>
/// construction is generic (the literal <c>"gh"</c> is only ever in the call to it, never in the
/// <c>ProcessStartInfo</c> shape itself), so neither scan below needs to name it explicitly.
/// </para>
/// </summary>
public sealed class GitHubSpawnSeamGuardTests
{
    private static readonly string[] RawSpawnMarkers =
    [
        "FileName = \"gh\"",
        "new ProcessStartInfo(\"gh\"",
        "ProcessStartInfo(fileName: \"gh\"",
    ];

    [Fact]
    public void No_production_file_other_than_ExternalProcess_builds_a_raw_gh_process()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();
        string allowedRelativePath = Path.Combine("Hall9k.Connectors", "Processes", "ExternalProcess.cs");

        string[] files =
        [
            .. Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(
                   Path.GetRelativePath(sourceDirectory, file), allowedRelativePath, StringComparison.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(sourceDirectory, file)),
        ];

        List<string> offenders = [];
        foreach (string file in files)
        {
            string code = File.ReadAllText(file);
            if (RawSpawnMarkers.Any(marker => code.Contains(marker, StringComparison.Ordinal)))
            {
                offenders.Add(Path.GetRelativePath(sourceDirectory, file));
            }
        }

        offenders.Should().BeEmpty(
            "only ExternalProcess.cs may build a ProcessStartInfo naming gh directly — everywhere "
            + "else reaches gh through the ProcessRunner/EnvironmentProcessRunner delegate seam, "
            + "which is what lets ProjectScopedGitHubRunner and ProjectGitHubClient pin an account "
            + "to the call");

        // Positive control: the marker shapes themselves, proven against a synthetic snippet
        // rather than a real file, since this migration's whole point was removing the last real
        // ones from the tree — a stale marker (gh's own raw-spawn shape drifting) would otherwise
        // leave this scan green while catching nothing.
        const string syntheticOffender = "process.StartInfo = new ProcessStartInfo(\"gh\") { };";
        RawSpawnMarkers.Any(marker => syntheticOffender.Contains(marker, StringComparison.Ordinal))
            .Should().BeTrue("the marker list must still catch the exact raw-spawn shape this task removed");
    }

    private static readonly string[] BareConstructionMarkers =
    [
        "new GitHubWorkItemProvider()",
        "new GitHubPullRequestProvider()",
        "new GitHubPullRequestSurface()",
        "new GitHubReviewAssignments()",
        "new GitHubReviewReplies()",
        "new GitHubReviewThreads()",
        "new GitHubPullRequestInspector()",
        "new GitHubRemoteParentReader()",
        "new TrackerClaimGate()",
        "new TrackerAssignmentTake()",
    ];

    /// <summary>
    /// Both named sites build the connector purely to call its synchronous, gh-free
    /// <c>WebUrl(ExternalReference)</c> — never <c>ImportAsync</c> or any other method that
    /// actually spawns gh — so an unaccounted <c>ProcessRunner</c> default there is inert rather
    /// than a live migration gap.
    /// </summary>
    private static readonly string[] AllowedBareConstructionRelativePaths =
    [
        Path.Combine("Hall9k.Cli", "Commands", "TaskLinkIssueCommand.cs"),
        Path.Combine("Hall9k.Daemon", "Review", "PrReviewEngine.cs"),
    ];

    [Fact]
    public void No_gh_connector_is_constructed_bare_outside_the_two_WebUrl_only_sites()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();

        string[] files =
        [
            .. Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !AllowedBareConstructionRelativePaths.Contains(
                   Path.GetRelativePath(sourceDirectory, file), StringComparer.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(sourceDirectory, file)),
        ];

        List<string> offenders = [];
        foreach (string file in files)
        {
            (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));
            string relativePath = Path.GetRelativePath(sourceDirectory, file);

            if (!balanced)
            {
                offenders.Add(
                    $"{relativePath} <StripCommentsAndStrings desynced on this file: stripped brace "
                    + "depth never returned to zero, so its construction coverage cannot be trusted>");
                continue;
            }

            if (BareConstructionMarkers.Any(marker => code.Contains(marker, StringComparison.Ordinal)))
            {
                offenders.Add(relativePath);
            }
        }

        offenders.Should().BeEmpty(
            "every GitHub or tracker connector is constructed with an explicit ProcessRunner "
            + "(ProjectScopedGitHubRunner in production) at the one place it is built for real — a "
            + "bare, zero-argument construction defaults back to ExternalProcess.Runner, spawning "
            + "gh under whichever account the machine happens to be logged into instead of the "
            + "project's own");

        // Positive control: the two allowed sites really do construct their connector bare, so a
        // regression in StripCommentsAndStrings or a stale marker (a connector renamed, its
        // parameterless constructor removed) cannot leave this scan green by no longer seeing
        // anything at all rather than by there being nothing left to see.
        foreach (string allowedRelativePath in AllowedBareConstructionRelativePaths)
        {
            (string allowedCode, _, _) = TestSourceTree.StripCommentsAndStrings(
                File.ReadAllText(Path.Combine(sourceDirectory, allowedRelativePath)));
            bool scanStillSeesTheAllowedConstruction =
                BareConstructionMarkers.Any(marker => allowedCode.Contains(marker, StringComparison.Ordinal));

            scanStillSeesTheAllowedConstruction.Should().BeTrue(
                $"{allowedRelativePath} does construct a connector bare (for its own WebUrl-only "
                + "use), so this scan must still be able to detect one there");
        }
    }
}
