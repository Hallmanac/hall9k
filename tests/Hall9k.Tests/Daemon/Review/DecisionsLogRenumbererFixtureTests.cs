using System.Text.RegularExpressions;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon.Review;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Daemon.Review;

/// <summary>
/// Drives <see cref="DecisionsLogRenumberer"/> directly against a real git repository fixture —
/// no daemon, no Postgres, no agent session — standing in for the shape a unit test cannot
/// otherwise exercise: two installs, each with its own Postgres and no shared allocator, merging
/// branches in their own order against one shared repository (Decisions Log
/// #162; walked with Brian 2026-09-07 20:11-20:15 EDT).
/// Ten branches all fork from the same commit, each appending exactly one placeholder Decisions
/// Log entry and one citation of it in its own file, and are fed through the renumberer one at a
/// time in a shuffled order — the same "this branch's own rebase just landed cleanly" state
/// <c>ReviewEngine.EnsureRebasedBeforeFinalPassAsync</c> hands the renumberer in production,
/// reached here by committing each branch's change directly onto the evolving tip rather than by
/// fighting git's own merge algorithm over ten branches that all touch the same few lines of
/// PLAN.md — the renumberer under test is oblivious to how its caller got there. The nine
/// citation-site files (docs/scope.md carrying two, the rest one each, for the ten sites) mirror
/// the real ones the Windows window recorded stale on 2026-09-07, so the citation rewrite runs
/// against realistic prose and code-comment shapes rather than a single synthetic pattern.
/// </summary>
public sealed class DecisionsLogRenumbererFixtureTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"hall9k-dlr-{Guid.NewGuid():N}");

    public DecisionsLogRenumbererFixtureTests() => Directory.CreateDirectory(_repoPath);

    public void Dispose() => TemporaryTree.TryDelete(_repoPath);

    private readonly record struct CitationSite(string RelativePath, string AnchorMarker, Func<string, string> RenderCitation);

    private readonly record struct BranchFixture(string TaskShortId, CitationSite Site, string EntryText);

    [Fact]
    public async Task Ten_branches_from_one_fork_point_renumber_dense_and_gap_free_in_shuffled_merge_order()
    {
        await RunGitAsync(["init", "-q", "-b", "main"]);
        await RunGitAsync(["config", "user.email", "test@hall9k.local"]);
        await RunGitAsync(["config", "user.name", "Hall9k Test"]);

        WriteBaselineFiles();
        await RunGitAsync(["add", "-A"]);
        await RunGitAsync(["commit", "-q", "-m", "fork point"]);
        string forkPointSha = (await RunGitCapturingAsync(["rev-parse", "HEAD"])).Trim();

        BranchFixture[] branches = BuildTenBranchFixtures();
        int[] order = ShuffledIndices(branches.Length, seed: 42);

        Dictionary<string, int> assignedNumbers = [];
        foreach (int index in order)
        {
            BranchFixture branch = branches[index];

            AppendPlaceholderEntryToPlan(branch);
            AppendCitationToSite(branch);

            await RunGitAsync(["add", "-A"]);
            await RunGitAsync(["commit", "-q", "-m", $"task {branch.TaskShortId}: append Decisions Log entry"]);

            // Every branch here takes the placeholder shape, which needs no base-tip filter at
            // all (its own token is unique by construction) — forkPointSha stands in for both
            // parameters since baseTipSha is simply unread on this path.
            DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
                ExternalProcess.Runner, _repoPath, forkPointSha, forkPointSha, branch.TaskShortId, CancellationToken.None);

            result.Outcome.Should().Be(
                DecisionsLogRenumberOutcome.Renumbered, $"task {branch.TaskShortId}'s own placeholder was at the tail");
            result.NewNumber.Should().NotBeNull();
            assignedNumbers[branch.TaskShortId] = result.NewNumber!.Value;
        }

        // Dense and gap-free: the fixture seeded entries 1-3, so the ten branches take 4..13,
        // each landing exactly where its own turn in the shuffled merge order says it should.
        assignedNumbers.Count.Should().Be(10);
        assignedNumbers.Values.Order().Should().Equal(Enumerable.Range(4, 10), "the ten assigned numbers must be dense and gap-free");
        for (int turn = 0; turn < order.Length; turn++)
        {
            assignedNumbers[branches[order[turn]].TaskShortId].Should().Be(4 + turn,
                $"branch {branches[order[turn]].TaskShortId} was the {turn + 1}(th) to be rebased in this shuffled order");
        }

        string plan = await File.ReadAllTextAsync(Path.Combine(_repoPath, "PLAN.md"));
        foreach (BranchFixture branch in branches)
        {
            // The placement note deliberately keeps naming the old placeholder as history (the
            // same way a pre-convention entry's own note used to keep naming its old real
            // number) — what must be gone is every CITATION of it, the '#PLACEHOLDER-…' form.
            plan.Should().NotContain($"#PLACEHOLDER-{branch.TaskShortId}", "every citation of the placeholder was rewritten");
        }

        List<int> allHeadingNumbers =
            [.. Regex.Matches(plan, @"^(\d+)\. \*\*", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value))];
        allHeadingNumbers.Should().OnlyHaveUniqueItems("no two entries may share a number after every branch is renumbered");
        allHeadingNumbers.Order().Should().Equal(Enumerable.Range(1, 13), "the log must be dense and gap-free from #1 through the last assigned number");

        foreach (BranchFixture branch in branches)
        {
            string siteContent = await File.ReadAllTextAsync(Path.Combine(_repoPath, branch.Site.RelativePath));
            siteContent.Should().Contain(
                $"#{assignedNumbers[branch.TaskShortId]}", $"{branch.Site.RelativePath} cites task {branch.TaskShortId}'s own entry");
            siteContent.Should().NotContain($"PLACEHOLDER-{branch.TaskShortId}");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens: a Decisions Log entry routinely runs
    /// to several blank-line-separated paragraphs after its own heading (e.g. #59 in PLAN.md
    /// itself), and a renumbering rebuild that assumes a one-line entry silently discards every
    /// paragraph between the heading and the section's own trailing divider. This pins that the
    /// body survives, word for word, with only the heading's own leading token changed.
    /// </summary>
    [Fact]
    public async Task A_multi_paragraph_placeholder_entrys_body_survives_renumbering()
    {
        await RunGitAsync(["init", "-q", "-b", "main"]);
        await RunGitAsync(["config", "user.email", "test@hall9k.local"]);
        await RunGitAsync(["config", "user.name", "Hall9k Test"]);

        string taskShortId = "6df5f975";
        WriteFile("PLAN.md", string.Join('\n',
        [
            "# Fixture Plan",
            "",
            "## 16. v0 Decisions Log",
            "",
            "1. **First seeded decision.** Baseline text.",
            "",
            $"PLACEHOLDER-{taskShortId}. **A multi-paragraph decision.** First paragraph, the heading's own sentence.",
            "",
            "**Second paragraph.** Body text that must survive renumbering untouched.",
            "",
            "**Third paragraph.** More body text, also must survive.",
            "",
            "---",
            "",
            "## 17. Reference Materials",
            "",
        ]));
        await RunGitAsync(["add", "-A"]);
        await RunGitAsync(["commit", "-q", "-m", "fork point"]);
        string forkPointSha = (await RunGitCapturingAsync(["rev-parse", "HEAD"])).Trim();

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            ExternalProcess.Runner, _repoPath, forkPointSha, forkPointSha, taskShortId, CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.Renumbered);
        result.NewNumber.Should().Be(2);

        string plan = await File.ReadAllTextAsync(Path.Combine(_repoPath, "PLAN.md"));
        plan.Should().Contain("2. **A multi-paragraph decision.** First paragraph, the heading's own sentence.");
        plan.Should().Contain("**Second paragraph.** Body text that must survive renumbering untouched.",
            "the entry's own second paragraph must not be discarded by the renumbering rebuild");
        plan.Should().Contain("**Third paragraph.** More body text, also must survive.",
            "the entry's own third paragraph must not be discarded by the renumbering rebuild");
        plan.Should().Contain("Renumbering placement note:", "the mechanically generated note must still be appended");
    }

    private static readonly string[] CitationSitePaths =
    [
        "docs/scope.md",
        "docs/scope.md",
        "docs/concepts.md",
        "AGENTS.md",
        "src/Hall9k.Cli/Orchestrator/LaunchAnchorDocument.cs",
        "src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs",
        "src/Hall9k.Cli/Commands/TaskShowCommand.cs",
        "src/Hall9k.Daemon/Closeout/CloseoutEngine.cs",
        "src/Hall9k.Daemon/Closeout/IPullRequestInspector.cs",
        "src/Hall9k.Daemon/Review/ReviewEngine.cs",
    ];

    private static BranchFixture[] BuildTenBranchFixtures()
    {
        Func<string, string>[] citationForms =
        [
            n => $"Decisions Log #{n}",
            n => $"PLAN.md §16 #{n}",
            n => $"the log #{n} entry",
            n => $"(#{n})",
        ];

        BranchFixture[] branches = new BranchFixture[10];
        for (int i = 0; i < 10; i++)
        {
            string shortId = $"aaaaaaa{i}";
            string relativePath = CitationSitePaths[i];
            bool isCode = relativePath.EndsWith(".cs", StringComparison.Ordinal);
            string anchor = isCode ? $"// CITATION-SITE:{i}" : $"<!-- CITATION-SITE:{i} -->";
            Func<string, string> form = citationForms[i % citationForms.Length];

            Func<string, string> render = isCode
                ? n => $"    // Cites task {shortId}'s own decision — {form(n)}."
                : n => $"Cites task {shortId}'s own decision — {form(n)}.";

            branches[i] = new BranchFixture(
                shortId,
                new CitationSite(relativePath, anchor, render),
                $"Branch {shortId}'s own decision, entry text distinct from every other branch's.");
        }

        return branches;
    }

    private void WriteBaselineFiles()
    {
        WriteFile("PLAN.md", string.Join('\n',
        [
            "# Fixture Plan",
            "",
            "## 16. v0 Decisions Log",
            "",
            "1. **First seeded decision.** Baseline text.",
            "2. **Second seeded decision.** Baseline text.",
            "3. **Third seeded decision.** Baseline text.",
            "",
            "---",
            "",
            "## 17. Reference Materials",
            "",
        ]));

        WriteFile("docs/scope.md", string.Join('\n',
        [
            "# Scope",
            "",
            "Works today, in prose form.",
            "",
            "<!-- CITATION-SITE:0 -->",
            "",
            "More prose about designed-but-unbuilt work.",
            "",
            "<!-- CITATION-SITE:1 -->",
            "",
        ]));

        WriteFile("docs/concepts.md", BaselineMarkdown("Concepts", 2));
        WriteFile("AGENTS.md", BaselineMarkdown("Agents", 3));
        WriteFile("src/Hall9k.Cli/Orchestrator/LaunchAnchorDocument.cs", BaselineCSharp("LaunchAnchorDocument", 4));
        WriteFile("src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs", BaselineCSharp("WorkPromptBuilder", 5));
        WriteFile("src/Hall9k.Cli/Commands/TaskShowCommand.cs", BaselineCSharp("TaskShowCommand", 6));
        WriteFile("src/Hall9k.Daemon/Closeout/CloseoutEngine.cs", BaselineCSharp("CloseoutEngine", 7));
        WriteFile("src/Hall9k.Daemon/Closeout/IPullRequestInspector.cs", BaselineCSharp("IPullRequestInspector", 8));
        WriteFile("src/Hall9k.Daemon/Review/ReviewEngine.cs", BaselineCSharp("ReviewEngine", 9));
    }

    private static string BaselineMarkdown(string title, int siteIndex) => string.Join('\n',
    [
        $"# {title}",
        "",
        "Some baseline prose that this branch's own citation lands beside.",
        "",
        $"<!-- CITATION-SITE:{siteIndex} -->",
        "",
    ]);

    private static string BaselineCSharp(string typeName, int siteIndex) => string.Join('\n',
    [
        "namespace Hall9k.Fixture;",
        "",
        $"public sealed class {typeName}",
        "{",
        "    public void Method()",
        "    {",
        $"        // CITATION-SITE:{siteIndex}",
        "    }",
        "}",
        "",
    ]);

    private void WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void AppendPlaceholderEntryToPlan(BranchFixture branch)
    {
        string path = Path.Combine(_repoPath, "PLAN.md");
        string[] lines = File.ReadAllLines(path);
        int dividerLine = Array.FindIndex(lines, line => line.Trim() == "---");
        dividerLine.Should().BeGreaterThan(0, "the fixture's own PLAN.md must still carry its trailing divider");

        List<string> rebuilt = [.. lines[..dividerLine]];
        rebuilt.Add($"PLACEHOLDER-{branch.TaskShortId}. **{branch.EntryText}**");
        rebuilt.Add("");
        rebuilt.AddRange(lines[dividerLine..]);

        File.WriteAllLines(path, rebuilt);
    }

    private void AppendCitationToSite(BranchFixture branch)
    {
        string path = Path.Combine(_repoPath, branch.Site.RelativePath);
        string content = File.ReadAllText(path);
        string citation = branch.Site.RenderCitation($"PLACEHOLDER-{branch.TaskShortId}");
        content.Should().Contain(branch.Site.AnchorMarker, $"{branch.Site.RelativePath} must still carry its own anchor marker");
        File.WriteAllText(path, content.Replace(branch.Site.AnchorMarker, $"{branch.Site.AnchorMarker}\n{citation}"));
    }

    private static int[] ShuffledIndices(int count, int seed)
    {
        int[] indices = [.. Enumerable.Range(0, count)];
        Random random = new(seed);
        for (int i = indices.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        return indices;
    }

    private async Task RunGitAsync(IReadOnlyList<string> arguments) => await RunGitCapturingAsync(arguments);

    private async Task<string> RunGitCapturingAsync(IReadOnlyList<string> arguments)
    {
        ProcessResult result = await ExternalProcess.Runner("git", arguments, _repoPath, CancellationToken.None);
        result.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} must succeed: {result.StandardError}");
        return result.StandardOutput;
    }
}
