using System.Text;
using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Every prompt builder returns <c>\n</c> as its only line terminator, so the same call yields the
/// same bytes on macOS and on windows-latest. The golden suites check the raw output of the
/// builders they pin (<see cref="PromptLineEndingAssertions"/>, before their own normalization);
/// this class checks the four builders no golden suite covers, and feeds carriage returns through
/// the input side of the others, which is the only way a test on any platform can see the
/// convention hold rather than the host's own line ending happen to agree with it.
/// </summary>
// Redirects PlatformPaths.Home to an empty temp home, for the reason QaReviewPromptTests states:
// a stale real install on this machine must never be what a template resolves from.
public sealed class PromptLineEndingsTests : IDisposable
{
    private const string WorktreePath = "/runs/some-run/worktree";

    private readonly ScopedTestHome _scopedHome = new();

    public void Dispose() => _scopedHome.Dispose();

    [Fact]
    public void Normalize_turns_every_carriage_return_form_into_a_line_feed()
    {
        PromptLineEndings.Normalize("a\r\nb\rc\nd").Should().Be("a\nb\nc\nd");
    }

    [Fact]
    public void Finish_normalizes_what_AppendLine_wrote_on_this_platform()
    {
        StringBuilder prompt = new();
        prompt.AppendLine("first").AppendLine("second");

        PromptLineEndings.Finish(prompt).Should().Be("first\nsecond\n");
    }

    [Fact]
    public void The_security_review_prompt_has_only_line_feeds()
    {
        ReviewPersonaPromptRequest request = new(
            QaReviewPromptTests.SomeTask(), QaReviewPromptTests.SomeProject(), "detached", "main",
            TimeSpan.FromMinutes(30));

        SecurityReviewPromptBuilder.Build(request).ShouldHaveOnlyLineFeeds();
    }

    [Fact]
    public void The_mention_follow_up_prompts_have_only_line_feeds()
    {
        PullRequestMentionComment comment = new(
            "IC_abc", "teammate", "Is the limiter reset deliberate?\r\nAnd is it tested?",
            "https://github.com/acme/web/pull/7#issuecomment-1",
            new DateTimeOffset(2026, 9, 14, 12, 30, 0, TimeSpan.Zero));

        MentionFollowUpPromptBuilder.Build(
            "acme/web", 7, WorktreePath, "main", comment, priorReport: "One line.\r\nTwo lines.",
            QaReviewPromptTests.SomeProject()).ShouldHaveOnlyLineFeeds();
        MentionFollowUpPromptBuilder.BuildMintAddendum(
            comment.AuthorLogin, comment.CreatedAt, comment.Body, comment.Url, WorktreePath, voiceSkill: null)
            .ShouldHaveOnlyLineFeeds();
    }

    [Fact]
    public void The_pull_request_review_preflight_prompt_has_only_line_feeds()
    {
        PrReviewPreflightPromptBuilder.Build(
            "acme/web#1", null, ["README.md", "src/App.cs"], ["src/App.cs"],
            matchedHunks: "@@ -1 +1 @@\r\n-old\r\n+new", "abc123headoid", "/runs/some-run/pull-request.diff")
            .ShouldHaveOnlyLineFeeds();
    }

    [Fact]
    public void The_courier_prompt_has_only_line_feeds()
    {
        CourierPromptBuilder.Build(
            "hall9k", ["37b5ec69  first item\r\n  continued"], "Send it as one message.\r\nVerbatim.")
            .ShouldHaveOnlyLineFeeds();
    }

    [Fact]
    public void A_pr_review_lens_prompt_has_only_line_feeds_including_its_appended_template_fragments()
    {
        TaskDetails task = QaReviewPromptTests.SomeTask();
        task.RetryReason = "Look again at the diff.\r\nIt changed.";

        AgentPromptBuilder.BuildPrReviewLens(
            task, QaReviewPromptTests.SomeProject(), "pr/42", ReviewLens.Conformance,
            baseBranch: "main").ShouldHaveOnlyLineFeeds();
    }

    [Fact]
    public void A_task_objective_pasted_with_carriage_returns_leaves_the_build_prompts_as_line_feeds()
    {
        TaskDetails task = QaReviewPromptTests.SomeTask();
        task.Objective = "Fix the retry loop.\r\nKeep the backoff exponential.\rDo not touch the schema.";
        task.AcceptanceCriteria = ["The loop backs off.\r\nIt gives up after five tries."];

        AgentPromptBuilder.Build(task, QaReviewPromptTests.SomeProject(), "task/1-slug", WorktreePath)
            .ShouldHaveOnlyLineFeeds();
        AgentPromptBuilder.BuildBudgetRetry(task).ShouldHaveOnlyLineFeeds();
    }
}
