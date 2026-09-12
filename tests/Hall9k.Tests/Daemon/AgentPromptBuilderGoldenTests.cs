using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Byte-for-byte (modulo line-ending normalization, since CI runs both ubuntu and windows) proof
/// that every one of <see cref="AgentPromptBuilder"/>'s public entry points renders exactly what it
/// did on main, across this task's move of its prose into <c>.claude/templates/agent-prompt-builder</c>
/// — the same discipline <c>ReviewLapPromptBuilderGoldenTests</c> established for the first migrated
/// builder. One fixture per entry point, captured from this branch's own first commit before any
/// prose moved into a template, and never hand-edited afterward — only regenerated
/// (<c>UPDATE_GOLDENS=1</c>) from a builder whose output is believed correct.
/// <para>
/// Every id, timestamp, and commit sha below is a fixed literal rather than <c>Guid.NewGuid()</c> or
/// <c>DateTimeOffset.UtcNow</c>: a golden fixture has to be byte-stable across the capture run and
/// every later comparison run, and these values are printed verbatim into the rendered prompt.
/// </para>
/// </summary>
// Redirects HALL9K_HOME to an empty temp home, same as ReviewLapPromptBuilderGoldenTests and for the
// identical reason: PromptTemplates falls back to TemplateLibraryPaths.CanonicalDirectory (a
// HALL9K_HOME-derived path) whenever this checkout's own .claude/templates does not carry a file it
// asks for, so a stale real install on this machine must never be what these fixtures read.
[Collection("Hall9kHome")]
public sealed class AgentPromptBuilderGoldenTests : IDisposable
{
    private readonly string _platformHome = Path.Combine(Path.GetTempPath(), $"h9k-apb-golden-home-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    private static readonly Guid TaskId = Guid.Parse("01a09285-6a21-7259-a883-c5ebe5482c3e");
    private static readonly Guid RunId = Guid.Parse("01a09285-7b32-7259-a883-c5ebe5482c3e");
    private static readonly DateTimeOffset FixedInstant = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // A fixed, literal path rather than _worktreePath: both Build and BuildCardPublication print
    // this value verbatim into the rendered prompt, and _worktreePath carries a fresh Guid every
    // test run — a golden fixture captured against one run's random path would never match the
    // next run's. Never created on disk; WorkPromptBuilder.ReadSkills reads Directory.Exists and
    // returns empty for a path that is not there, so no skills section renders either way.
    private const string FixedWorktreePath = "/home/agent/.hall9k/projects/hall9k/repo/wt-golden-fixture";

    public AgentPromptBuilderGoldenTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _platformHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_platformHome))
        {
            Directory.Delete(_platformHome, recursive: true);
        }
    }

    [Fact]
    public void Build_matches_its_golden() =>
        AssertMatchesGolden("build", AgentPromptBuilder.Build(SomeTask(), SomeProject(), "task/1-slug", FixedWorktreePath));

    [Fact]
    public void BuildFollowUp_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        task.FollowUpReason = "The pull request's CI review thread asked for a retry with more context.";
        task.InteractiveModeEnabled = true;
        string prompt = AgentPromptBuilder.BuildFollowUp(
            task, SomeProject(), "task/1-slug", "https://github.com/acme/web/pull/7", CommitStyle.Narrative,
            interactiveMilestoneAddress: "agent://milestones/1");
        AssertMatchesGolden("build-follow-up", prompt);
    }

    [Fact]
    public void BuildReviewRequestedChanges_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        task.FollowUpReason = "A human formally requested changes on the open pull request.";
        task.ChangesRequestedReviews =
        [
            new ChangesRequestedReview(
                "octocat", "https://github.com/acme/web/pull/7#pullrequestreview-42", FixedInstant,
                [
                    new ChangesRequestedFinding("The retry loop never resets its backoff.", "src/Limiter.cs:42", "PRRT_abc"),
                    new ChangesRequestedFinding("Please also explain why this endpoint is exempt.", null, null),
                ]),
            new ChangesRequestedReview("teammate", "https://github.com/acme/web/pull/7#pullrequestreview-43", null, []),
        ];
        string prompt = AgentPromptBuilder.BuildReviewRequestedChanges(
            task, SomeProject(), "task/1-slug", "https://github.com/acme/web/pull/7", CommitStyle.Narrative);
        AssertMatchesGolden("build-review-requested-changes", prompt);
    }

    [Fact]
    public void BuildFixChecks_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        task.FollowUpReason = "The pull request's CI checks are failing.";
        string prompt = AgentPromptBuilder.BuildFixChecks(
            task, SomeProject(), "task/1-slug", "https://github.com/acme/web/pull/7", CommitStyle.Append);
        AssertMatchesGolden("build-fix-checks", prompt);
    }

    [Fact]
    public void BuildRebase_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        task.FollowUpReason = "The pull request's branch now conflicts with main.";
        string prompt = AgentPromptBuilder.BuildRebase(
            task, SomeProject(), "task/1-slug", "https://github.com/acme/web/pull/7", CommitStyle.Narrative,
            humanResolution: "Keep the newer retry-budget constant; the old ceiling was a stopgap.");
        AssertMatchesGolden("build-rebase", prompt);
    }

    [Fact]
    public void BuildPreFinalPassRebase_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        string prompt = AgentPromptBuilder.BuildPreFinalPassRebase(
            task, SomeProject(), "task/1-slug", CommitStyle.Narrative, pullRequestUrl: null,
            humanResolution: "Keep both changes; neither supersedes the other.", rebaseStillInProgress: true);
        AssertMatchesGolden("build-pre-final-pass-rebase", prompt);
    }

    [Fact]
    public void BuildSettlingGateRepair_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        string prompt = AgentPromptBuilder.BuildSettlingGateRepair(
            task, SomeProject(), "task/1-slug", CommitStyle.Narrative, "https://github.com/acme/web/pull/7",
            "main", "aaaaaaaaaa1111111111", "bbbbbbbbbb2222222222", rebaseWasRecovered: true,
            gateOutput: "dotnet test failed: 3 tests failed in Hall9k.Tests.",
            humanGuidance: "The failure is a real regression from the rebase; fix it, do not revert.");
        AssertMatchesGolden("build-settling-gate-repair", prompt);
    }

    [Fact]
    public void BuildStackReplay_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        string prompt = AgentPromptBuilder.BuildStackReplay(
            task, SomeProject(), "task/2-child-slug", "https://github.com/acme/web/pull/9", CommitStyle.Narrative,
            "task/1-parent-slug", "cccccccccc3333333333", "dddddddddd4444444444");
        AssertMatchesGolden("build-stack-replay", prompt);
    }

    [Fact]
    public void BuildReview_conformance_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        string prompt = AgentPromptBuilder.BuildReview(
            task, SomeProject(), "task/1-slug", cycle: 2, ReviewLens.Conformance, ReviewMode.Discovery,
            priorRulings: [new ReviewParkResolution(1, ReviewVerdict.MergeReady, "The reviewer's read was wrong; git history proves it.", FixedInstant)],
            priorHumanDirectedInteractions: [new ExternalInteractionRecord(FixedInstant, "Brian", "Skip the workaround.", true, "The workaround is no longer needed.")],
            priorBoundaryApprovals: [new BoundaryApprovalRecord(FixedInstant)],
            priorHumanFixes: [new HumanFixRecord(1, null, FixedInstant)]);
        AssertMatchesGolden("build-review-conformance", prompt);
    }

    [Fact]
    public void BuildReview_adversarial_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        string prompt = AgentPromptBuilder.BuildReview(
            task, SomeProject(), "task/1-slug", cycle: 1, ReviewLens.Adversarial, ReviewMode.FinalFullPass);
        AssertMatchesGolden("build-review-adversarial", prompt);
    }

    [Fact]
    public void BuildPrReviewLens_matches_its_golden()
    {
        TaskDetails task = SomePrReviewTask();
        string prompt = AgentPromptBuilder.BuildPrReviewLens(
            task, SomeProject(), "task/1-slug", ReviewLens.Conformance, "develop");
        AssertMatchesGolden("build-pr-review-lens", prompt);
    }

    [Fact]
    public void BuildReviewVerify_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        string prompt = AgentPromptBuilder.BuildReviewVerify(
            task, SomeProject(), "task/1-slug", cycle: 3, [ReviewLens.Conformance, ReviewLens.Adversarial],
            priorFindings: "FINDING: severity=high; scope=in-scope; at=src/Limiter.cs:42\nThe limiter never resets.",
            priorFixPosition: "Fixed the reset bug and added a regression test.",
            sinceSha: "eeeeeeeeee5555555555", priorCycleMode: ReviewMode.Discovery, priorCycleSinceSha: null);
        AssertMatchesGolden("build-review-verify", prompt);
    }

    [Fact]
    public void BuildReviewVerdictReprompt_matches_its_golden()
    {
        string prompt = AgentPromptBuilder.BuildReviewVerdictReprompt(SomeProject(), cycle: 2, ReviewMode.Discovery);
        AssertMatchesGolden("build-review-verdict-reprompt", prompt);
    }

    [Fact]
    public void BuildBudgetRetry_matches_its_golden() =>
        AssertMatchesGolden("build-budget-retry", AgentPromptBuilder.BuildBudgetRetry(SomeTask()));

    [Fact]
    public void BuildSessionErrorRetry_matches_its_golden() =>
        AssertMatchesGolden("build-session-error-retry", AgentPromptBuilder.BuildSessionErrorRetry(SomeTask()));

    [Fact]
    public void BuildUncommittedWorkRecovery_matches_its_golden()
    {
        string prompt = AgentPromptBuilder.BuildUncommittedWorkRecovery(
            SomeTask(), ["src/Limiter.cs", "tests/LimiterTests.cs", "docs/rate-limiting.md"],
            priorSessionReportedBackgroundWait: true);
        AssertMatchesGolden("build-uncommitted-work-recovery", prompt);
    }

    [Fact]
    public void BuildReviewFix_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        task.InteractiveModeEnabled = true;
        string prompt = AgentPromptBuilder.BuildReviewFix(
            task, SomeProject(), "task/1-slug",
            findings: "FINDING: severity=high; scope=in-scope; at=src/Limiter.cs:42\nThe limiter never resets.",
            cycle: 2, interactiveSessionAddress: "agent://milestones/1");
        AssertMatchesGolden("build-review-fix", prompt);
    }

    [Fact]
    public void BuildContextSynthesis_matches_its_golden()
    {
        string prompt = AgentPromptBuilder.BuildContextSynthesis(
            SomeTask(), blockerCount: 3,
            blockerContext: "### Blocker one\n\nShipped the shared mechanism.\n\n### Blocker two\n\nShipped the exemplar migration.");
        AssertMatchesGolden("build-context-synthesis", prompt);
    }

    [Fact]
    public void BuildCardPublication_matches_its_golden()
    {
        TaskDetails task = SomeTask();
        task.CurrentRunId = RunId;
        task.AgentContext = "Filed from a Slack thread about rate limiting.";
        string prompt = AgentPromptBuilder.BuildCardPublication(
            task, SomeProject(), FixedWorktreePath, "acme.atlassian.net", JiraProjectKey.Parse("PROJ"),
            "h9k task write-jira", routingGuidance: "File epics first, then ask for the parent.");
        AssertMatchesGolden("build-card-publication", prompt);
    }

    private static void AssertMatchesGolden(string name, string actual)
    {
        string path = GoldenPath(name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"golden fixture {path} must be captured before this test can run");
        string expected = File.ReadAllText(path);
        Normalize(actual).Should().Be(Normalize(expected));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string GoldenPath(string name) =>
        Path.Combine(RepositoryRoot(), "tests", "Hall9k.Tests", "Fixtures", "PromptGoldens",
            "AgentPromptBuilder", $"{name}.golden.txt");

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find this checkout's own Hall9k.slnx above " + AppContext.BaseDirectory);
    }

    private static TaskDetails SomeTask() => new()
    {
        Id = TaskId,
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429", "A 429 response includes Retry-After"],
        CurrentRunId = RunId,
    };

    private static TaskDetails SomePrReviewTask() => new()
    {
        Id = TaskId,
        Type = TaskType.PrReview,
        Objective = "Review pull request #42",
        AcceptanceCriteria = ["Every finding is verified before it is reported"],
        CurrentRunId = RunId,
    };

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
        ContextLinks = [new ContextLink("Jira board", new Uri("https://example.atlassian.net/board"))],
        VerifyCommands = [new VerifyCommand("build", "dotnet build"), new VerifyCommand("test", "dotnet test")],
        WritingConventions = WritingConventions.Default,
    };
}
