using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Prompts;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The hook that makes the reply park enforcement rather than instruction (task: a
/// review-feedback follow-up never answers a human reviewer in the owner's name on its own): it
/// refuses the shell routes that write inside a review thread, so the only way a follow-up
/// reaches one is <c>h9k pr reply</c>, which knows whose thread it is.
/// </summary>
public sealed class ReviewThreadReplyGuardTests
{
    /// <summary>
    /// The quoted form is the one the resolve-review-threads skill actually teaches, and the
    /// reason this is a hook rather than a <c>permissions.deny</c> entry: a prefix rule matches
    /// none of these spellings.
    /// </summary>
    [Theory]
    [InlineData("gh api \"repos/$SLUG/pulls/$PR_NUMBER/comments/$COMMENT_ID/replies\" -f body=\"...\"")]
    [InlineData("gh api repos/acme/web/pulls/7/comments/12345/replies -f body='no'")]
    [InlineData("gh api graphql -f query='mutation{ addPullRequestReviewThreadReply(input:{...}) }'")]
    [InlineData("gh api graphql -f query='mutation{ addPullRequestReviewThread(input:{...}) }'")]
    [InlineData("gh api graphql -f query='mutation{ addPullRequestReviewComment(input:{...}) }'")]
    [InlineData("gh api graphql -f query='mutation{ addPullRequestReview(input:{...}) }'")]
    [InlineData("gh pr review 7 --comment --body 'x'")]
    public void A_command_that_writes_into_a_review_thread_is_refused(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeTrue();

    /// <summary>
    /// The REST inline-comment endpoint, which this repository's own walk-pr-review-findings skill
    /// teaches with <c>in_reply_to</c> and which starts a thread with <c>commit_id</c>, plus the
    /// review endpoint this platform's own poster submits through. All three share a path a GET
    /// also answers, so the write parameter is what decides — and all three were holes the first
    /// time round (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Theory]
    [InlineData("gh api repos/acme/web/pulls/7/comments -f body=\"no\" -F in_reply_to=12345")]
    [InlineData("gh api \"repos/$SLUG/pulls/$PR_NUMBER/comments\" -F in_reply_to=$REPLY_ID -f body='no'")]
    [InlineData(
        "gh api repos/acme/web/pulls/7/comments -f body=no -f commit_id=abc123 -f path=src/A.cs -F line=4")]
    [InlineData("gh api --method POST repos/acme/web/pulls/7/reviews -f event=COMMENT -f body=no")]
    [InlineData(
        "curl -X POST -d '{\"body\":\"no\"}' https://api.github.com/repos/acme/web/pulls/7/comments/1/replies")]
    public void The_rest_write_endpoints_under_a_pull_request_are_refused_too(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeTrue();

    /// <summary>
    /// Naming a route is not calling one. Every identifier this guard matches on is also source
    /// text IN this repository, so a follow-up that searches for one, or commits a fix mentioning
    /// one, was refused for what it never sent (independent pre-PR review, cycle 1, adversarial
    /// lens). The read half of the two paths that answer a GET is here for the same reason: a
    /// session finds a thread's numeric reply id by listing a pull request's review comments.
    /// </summary>
    [Theory]
    [InlineData("git grep addPullRequestReviewThreadReply src/")]
    [InlineData("rg \"comments/.*/replies\" src/Hall9k.Connectors")]
    [InlineData("git commit -m \"fix: escape addPullRequestReviewThreadReply body\"")]
    [InlineData("bash -c 'grep -rn addPullRequestReview src/'")]
    [InlineData("gh api repos/acme/web/pulls/7/comments --jq '.[].id'")]
    [InlineData("gh api repos/acme/web/pulls/7/reviews --jq '.[].state'")]
    public void A_command_that_only_names_a_route_runs(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();

    /// <summary>
    /// Everything a lap legitimately does keeps working. <c>gh pr comment</c> is on this list
    /// deliberately and is named as a hole in <see cref="ReviewThreadReplyRoutes"/>'s own doc: a
    /// review BODY is unthreadable, and answering one has been an instruction since Decisions
    /// Log #62.
    /// </summary>
    [Theory]
    [InlineData("gh api graphql -f query='query($owner:String!){ repository { pullRequest { reviewThreads { nodes { id } } } } }'")]
    [InlineData("gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread { isResolved } } }'")]
    [InlineData("gh pr view 7 --json reviews")]
    [InlineData("gh pr diff 7")]
    [InlineData("gh pr comment 7 --body 'answering the review body'")]
    [InlineData("h9k pr reply 28b19893 --thread PRRT_1 --disposition fix --body \"done\"")]
    [InlineData("dotnet test")]
    public void Everything_else_runs(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();

    [Fact]
    public void The_hook_denies_a_bash_call_that_posts_into_a_thread() =>
        PullRequestReplyGuardCommand.Denies(Payload(
            "Bash", "gh api \"repos/a/b/pulls/7/comments/1/replies\" -f body=x")).Should().BeTrue();

    [Fact]
    public void The_hook_allows_a_bash_call_that_reads() =>
        PullRequestReplyGuardCommand.Denies(Payload("Bash", "gh pr view 7 --json reviews")).Should().BeFalse();

    /// <summary>
    /// Failing open is the deliberate choice, and it is worth a test of its own: a guard that
    /// failed closed would block every command in every follow-up the first time Claude Code
    /// changed the payload shape under it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"tool_name\":\"Bash\"}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{}}")]
    [InlineData("{\"tool_input\":{\"command\":123}}")]
    public void An_unreadable_payload_lets_the_call_run(string? payload) =>
        PullRequestReplyGuardCommand.Denies(payload).Should().BeFalse();

    /// <summary>
    /// The matcher says Bash, but a settings file an operator edited could say otherwise, and a
    /// guard reading another tool's input as a shell command would refuse on text it cannot
    /// interpret.
    /// </summary>
    [Fact]
    public void A_call_to_another_tool_is_never_refused() =>
        PullRequestReplyGuardCommand.Denies(Payload(
            "Write", "gh api \"repos/a/b/pulls/7/comments/1/replies\"")).Should().BeFalse();

    /// <summary>
    /// The refusal is what the session reads, so it has to name the route that IS allowed — an
    /// agent that cannot self-correct from the message retries the same command.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_allowed_route_and_survives_serialization()
    {
        ReviewThreadReplyRoutes.RefusalReason.Should().Contain("h9k pr reply");
        ReviewThreadReplyRoutes.RefusalReason.Should().Contain("RESOLUTION: disputed");

        using JsonDocument denial = JsonDocument.Parse(PullRequestReplyGuardCommand.DenialJson());
        JsonElement output = denial.RootElement.GetProperty("hookSpecificOutput");
        output.GetProperty("hookEventName").GetString().Should().Be("PreToolUse");
        output.GetProperty("permissionDecision").GetString().Should().Be("deny");
        output.GetProperty("permissionDecisionReason").GetString().Should().Be(
            ReviewThreadReplyRoutes.RefusalReason, "the reason reaches the model verbatim or not at all");
    }

    /// <summary>
    /// The guard only ships where it belongs: a follow-up run's settings carry the hook, a fresh
    /// build session's do not.
    /// </summary>
    [Fact]
    public void Only_a_guarded_spawn_writes_the_hook_into_its_settings()
    {
        ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)).Should().NotContain("reply-guard");
        string guarded = ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30), guardReviewThreadReplies: true);
        guarded.Should().Contain("\"PreToolUse\"").And.Contain("h9k pr reply-guard");
        using JsonDocument settings = JsonDocument.Parse(guarded);
        settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse").GetArrayLength().Should().Be(
            1, "a settings file Claude Code cannot parse installs no guard at all");
    }

    private static string Payload(string tool, string command) => JsonSerializer.Serialize(new
    {
        hook_event_name = "PreToolUse",
        tool_name = tool,
        tool_input = new { command },
    });
}
