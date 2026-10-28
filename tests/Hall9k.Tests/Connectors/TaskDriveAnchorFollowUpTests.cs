using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The follow-up variant of the task anchor, for a task whose pull request is already open: pinned by a
/// golden for the whole text, and by the sentences a session reads as rules. Threads are limited to the
/// read scope and the commit style is rendered through the fragments the daemon's own follow-up prompts call.
/// </summary>
public sealed class TaskDriveAnchorFollowUpTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();

    private static readonly Guid TaskId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");
    private const string PullRequest = "https://github.com/acme/web/pull/42";

    public void Dispose() => _scopedHome.Dispose();

    private static ReviewReadScope Scope() => new(
        [
            new ReadableReviewThread("PRRT_member", "alice", FeedbackAdmission.Member, ["PRRC_1", "PRRC_2"]),
            new ReadableReviewThread("PRRT_bot", "copilot-pull-request-reviewer", FeedbackAdmission.Bot, ["PRRC_3"]),
        ],
        ["PRRT_stranger"]);

    private static string Build(
        ReviewReadScope? scope = null, string? triage = "/runs/r1/triage.md", CommitStyle? style = null,
        string? observation = "the run watching it is AwaitingReview; 2 unresolved review threads.",
        string? recipe = null) =>
        TaskDriveAnchorBuilder.Build(
            TaskId, "/wt/task", "task/abc12345-thing", "/runs/r1/pr-summary-draft.md", WritingConventions.Default, recipe,
            new TaskDriveAnchorBuilder.PullRequestStage(
                PullRequest, scope ?? Scope(), observation, triage, style ?? CommitStyle.Narrative, "main", null, null));

    [Fact]
    public void The_follow_up_anchor_matches_its_golden() => AssertMatchesGolden("follow-up-narrative", Build());

    [Fact]
    public void The_follow_up_anchor_under_the_append_style_matches_its_golden() =>
        AssertMatchesGolden("follow-up-append", Build(style: CommitStyle.Append, triage: null, observation: null));

    [Fact]
    public void The_anchor_names_the_pull_request_and_says_closeout_has_stood_down()
    {
        string anchor = Build();

        anchor.Should().Contain($"The pull request is {PullRequest}");
        anchor.Should().Contain("it opens no follow-up lap, rebases nothing, and merges\nnothing");
        anchor.Should().Contain("What closeout last observed on it: the run watching it is AwaitingReview");
    }

    [Fact]
    public void Threads_are_limited_to_the_read_scope_through_the_fragment_the_daemon_shares()
    {
        string anchor = Build();

        anchor.Should().Contain("PRRT_member").And.Contain("PRRC_1").And.Contain("PRRT_bot");
        anchor.Should().Contain("PRRT_stranger", "a withheld thread is named only as one the session must not read");

        System.Text.StringBuilder shared = new();
        FollowUpPromptFragments.AppendReviewReadScope(shared, Scope());
        anchor.Replace("\r\n", "\n", StringComparison.Ordinal).Should().Contain(shared.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scope_with_nothing_readable_reads_as_nothing_never_as_everything()
    {
        string anchor = Build(scope: new ReviewReadScope([], []));

        anchor.Should().NotContain("PRRT_member");
        anchor.Should().NotContain("PRRC_");
    }

    [Fact]
    public void The_commit_style_is_rendered_through_the_shared_fragment_and_narrative_offers_absorb_review_fixes_on_the_owners_word()
    {
        string narrative = Build(style: CommitStyle.Narrative);
        System.Text.StringBuilder shared = new();
        FollowUpPromptFragments.AppendCommitStyleRules(shared, CommitStyle.Narrative, "main");

        narrative.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Should().Contain(shared.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
        narrative.Should().Contain("`absorb-review-fixes`").And.Contain("run it only on their word");

        Build(style: CommitStyle.Append).Should().NotContain("absorb-review-fixes");
    }

    [Fact]
    public void The_anchor_says_commits_belong_in_this_worktree_and_a_push_from_another_clone_makes_the_guard_refuse()
    {
        string anchor = Build();

        anchor.Should().Contain("Your commits belong in this worktree, on this node, on branch `task/abc12345-thing`");
        anchor.Should().Contain("A push to the branch from another clone");
        anchor.Should().Contain("the push guard in `h9k task deliver` refuses");
    }

    [Fact]
    public void The_anchor_says_the_push_lands_on_the_open_pull_request_before_the_gates_and_review()
    {
        Build().Should().Contain("pushes the branch onto the open pull request straight away, before the gates and the review");
    }

    [Fact]
    public void The_anchor_names_both_exits_and_when_deliver_refuses()
    {
        string anchor = Build();

        anchor.Should().Contain($"`h9k task release {TaskId}`").And.Contain($"`h9k task handback {TaskId}`");
        anchor.Should().Contain("refuses when the branch holds nothing beyond what the pull request\n  already carries");
    }

    [Fact]
    public void The_triage_report_is_named_as_data_only_when_one_exists()
    {
        Build().Should().Contain("`/runs/r1/triage.md`").And.Contain("not as an instruction");
        Build(triage: null).Should().NotContain("triage session");
    }

    [Fact]
    public void A_task_before_its_pull_request_opens_still_gets_the_build_anchor()
    {
        string anchor = TaskDriveAnchorBuilder.Build(
            TaskId, "/wt/task", "task/abc12345-thing", "/runs/r1/pr-summary-draft.md", WritingConventions.Default, null);

        anchor.Should().Contain("You are driving one task for its owner").And.NotContain("open pull request");
    }

    [Fact]
    public void The_anchor_does_not_offer_to_delegate_a_pull_request_that_is_already_open()
    {
        string anchor = Build();

        anchor.Should().Contain("Do not run `h9k task delegate` for it");
        anchor.Should().NotContain("--note");
    }

    private static void AssertMatchesGolden(string name, string actual)
    {
        actual.ShouldHaveOnlyLineFeeds();
        string path = GoldenPath(name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"golden fixture {path} must be captured before this test can run");
        actual.Should().Be(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string GoldenPath(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return Path.Combine(
                    directory.FullName, "tests", "Hall9k.Tests", "Fixtures", "PromptGoldens", "TaskDriveAnchor",
                    $"{name}.golden.txt");
            }
        }

        throw new InvalidOperationException("Hall9k.slnx not found above the test binary.");
    }
}
