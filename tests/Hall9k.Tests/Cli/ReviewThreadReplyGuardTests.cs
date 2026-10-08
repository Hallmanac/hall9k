using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Shared.ValueObjects;
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
    /// Rewriting what a thread already says, and removing a reviewer's words, are writes into it
    /// — and neither is a POST. A method test that read only <c>POST</c>, only with a space after
    /// the flag, let the whole edit-and-delete route through in every spelling the two clients
    /// accept (Copilot, PR #397): the single-comment path carries no pull-request number at all,
    /// <c>--method=</c> and <c>-X</c>-attached are one flag to the program, and a DELETE carries
    /// no body for the parameter test to catch instead.
    /// </summary>
    [Theory]
    [InlineData("gh api --method=PATCH repos/acme/web/pulls/comments/123 --input payload.json")]
    [InlineData("gh api -XDELETE repos/acme/web/pulls/comments/123")]
    [InlineData("gh api --method DELETE repos/acme/web/pulls/comments/123")]
    [InlineData("gh api -X PATCH \"repos/$SLUG/pulls/comments/$COMMENT_ID\" -f body='rewritten'")]
    [InlineData("gh api --method=PUT repos/acme/web/pulls/7/reviews --input review.json")]
    [InlineData("curl --request DELETE https://api.github.com/repos/acme/web/pulls/comments/123")]
    [InlineData("curl -XPOST https://api.github.com/repos/acme/web/pulls/7/reviews --data @review.json")]
    [InlineData("gh api graphql -f query='mutation{ updatePullRequestReviewComment(input:{...}) }'")]
    [InlineData("gh api graphql -f query='mutation{ deletePullRequestReviewComment(input:{...}) }'")]
    public void Editing_or_deleting_a_comment_already_in_a_thread_is_refused(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeTrue();

    /// <summary>
    /// The other half of that widening, which is the half that could have cost a lap its reads:
    /// a GET is not a mutating method however it is spelled, and the number-free comment path
    /// answers one too.
    /// </summary>
    [Theory]
    [InlineData("gh api --method GET repos/acme/web/pulls/comments/123")]
    [InlineData("gh api -X GET repos/acme/web/pulls/7/comments --jq '.[].id'")]
    [InlineData("gh api repos/acme/web/pulls/comments/123 --jq .body")]
    public void Reading_a_comment_is_still_never_a_write(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();

    /// <summary>
    /// <c>in_reply_to</c> and <c>commit_id</c> are response fields as well as request ones, and
    /// matching them as bare substrings refused the one read a session needs most: listing a
    /// thread's comments to find the numeric reply id, filtered on exactly those fields. The
    /// refusal told it to use <c>h9k pr reply</c>, which cannot list anything (independent pre-PR
    /// review, cycle 1, adversarial lens). Only an assignment is a write.
    /// </summary>
    [Theory]
    [InlineData("gh api repos/acme/web/pulls/7/comments --jq 'map(select(.in_reply_to_id == null))'")]
    [InlineData("gh api repos/acme/web/pulls/7/comments --jq '.[] | select(.in_reply_to == 12345)'")]
    [InlineData("gh api repos/acme/web/pulls/7/comments --jq '.[] | select(.commit_id==\"abc123\")'")]
    [InlineData("gh api repos/acme/web/pulls/7/comments --jq '{id, in_reply_to_id, commit_id}'")]
    public void Filtering_a_read_on_a_write_field_is_still_a_read(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();

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
    [InlineData("git commit -m \"mention gh pr review in the docs\"")]
    [InlineData("rg \"gh pr review\" docs")]
    [InlineData("gh api repos/acme/web/pulls/7/comments --jq '.[].id'")]
    [InlineData("gh api repos/acme/web/pulls/7/reviews --jq '.[].state'")]
    public void A_command_that_only_names_a_route_runs(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();

    /// <summary>
    /// Everything a lap legitimately does keeps working: its reads, its own thread resolves, and
    /// the platform's posting path itself. <c>gh pr comment</c> is no longer on this list (task: a
    /// dispatched session never speaks to a person at the top level of a pull request on its own);
    /// a review body is answered through <c>h9k pr reply --review</c> instead.
    /// </summary>
    [Theory]
    [InlineData("gh api graphql -f query='query($owner:String!){ repository { pullRequest { reviewThreads { nodes { id } } } } }'")]
    [InlineData("gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread { isResolved } } }'")]
    [InlineData("gh pr view 7 --json reviews")]
    [InlineData("gh pr diff 7")]
    [InlineData("h9k pr reply 28b19893 --thread PRRT_1 --disposition fix --body \"done\"")]
    [InlineData("h9k pr reply 28b19893 --review https://github.com/a/b/pull/7#pullrequestreview-9 --disposition fix --body \"done\"")]
    [InlineData("dotnet test")]
    public void Everything_else_runs(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();

    /// <summary>
    /// The top-level routes (task: a dispatched session never speaks to a person at the top level
    /// of a pull request on its own): the same class of message the two origin incidents were,
    /// reachable without going near a thread. Every spelling a session reaches for, program form
    /// and API form alike.
    /// </summary>
    public static TheoryData<string> TopLevelRoutes =>
    [
        "gh pr comment 7 --body 'answering the review body'",
        "gh issue comment 7 --body 'issues and pull requests share a number space'",
        "gh pr comment 7 -F body.md",
        "gh -R acme/web pr comment 7 --body x",
        "gh --repo acme/web issue comment 7 -b x",
        "GH_TOKEN=abc gh pr comment 7 --body x",
        "cd repo && gh pr comment 7 --body x",
        "git push; gh pr comment 7 --body x",
        "echo done | gh pr comment 7 --body-file -",
        "bash -c 'gh pr comment 7 --body x'",
        "sh -lc \"cd repo; gh issue comment 7 --body x\"",
        "pwsh -NoProfile -Command \"gh pr comment 7 --body x\"",
        "eval \"gh pr comment 7 --body x\"",
        "echo \"$(gh pr comment 7 --body x)\"",
        "echo `gh pr comment 7 --body x`",
        "& \"C:\\Program Files\\GitHub CLI\\gh.exe\" pr comment 7 --body x",
        "g\"h\" pr comment 7 --body x",
        "timeout 30 gh pr comment 7 --body x",
        "gh pr review 7 --comment --body 'x'",
        "cat <<EOF | bash\ngh pr comment 7 --body x\nEOF",
        "gh api repos/acme/web/issues/7/comments -f body='answering'",
        "gh api \"repos/$SLUG/issues/$PR_NUMBER/comments\" -f body=\"$TEXT\"",
        "gh api --method POST repos/acme/web/issues/7/comments --input comment.json",
        "gh api repos/acme/web/issues/7/comments --input comment.json",
        "gh api -X PATCH repos/acme/web/issues/comments/123 -f body='rewritten'",
        "gh api --method=DELETE repos/acme/web/issues/comments/123",
        "curl -X POST -H 'Authorization: token $T' -d '{\"body\":\"x\"}' https://api.github.com/repos/acme/web/issues/7/comments",
        "curl -d '{\"body\":\"x\"}' https://api.github.com/repos/acme/web/issues/7/comments",
        "gh api graphql -f query='mutation{ addComment(input:{subjectId:\"PR_1\", body:\"x\"}){ clientMutationId } }'",
        "gh api graphql -f query='mutation{ updateIssueComment(input:{id:\"IC_1\", body:\"x\"}){ clientMutationId } }'",
        "gh api graphql -f query='mutation{ deleteIssueComment(input:{id:\"IC_1\"}){ clientMutationId } }'",
    ];

    [Theory]
    [MemberData(nameof(TopLevelRoutes))]
    public void A_top_level_comment_route_is_refused(string command) =>
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeTrue();

    /// <summary>The hook itself, on both shell tools it is attached to, for every new route.</summary>
    [Theory]
    [MemberData(nameof(TopLevelRoutes))]
    public void The_hook_refuses_every_top_level_route_on_both_shell_tools(string command)
    {
        PullRequestReplyGuardCommand.Denies(Payload("Bash", command)).Should().BeTrue();
        PullRequestReplyGuardCommand.Denies(Payload("PowerShell", command)).Should().BeTrue();
    }

    /// <summary>
    /// Reads stay open, and so does text that only NAMES a route (a search pattern, a commit
    /// message, a heredoc body that feeds no shell, an echo). A regex over the raw text refused
    /// every one of these, which is the failure the class doc records for the API routes.
    /// </summary>
    [Theory]
    [InlineData("gh pr view 7 --comments")]
    [InlineData("gh pr view 7 --json comments --jq '.comments[].body'")]
    [InlineData("gh issue view 7 --comments")]
    [InlineData("gh api repos/acme/web/issues/7/comments")]
    [InlineData("gh api repos/acme/web/issues/7/comments --paginate --jq '.[].body'")]
    [InlineData("gh api -X GET repos/acme/web/issues/comments/123")]
    [InlineData("gh api \"repos/$SLUG/issues/$PR_NUMBER/comments\" --jq '.[] | select(.user.login == \"x\")'")]
    [InlineData("curl -s https://api.github.com/repos/acme/web/issues/7/comments")]
    [InlineData("git grep \"gh pr comment\" -- docs")]
    [InlineData("git grep -n 'gh issue comment' src/")]
    [InlineData("rg 'gh pr comment' .claude")]
    [InlineData("rg \"gh pr review|gh pr comment\" docs")]
    [InlineData("git commit -m \"docs: stop teaching gh pr comment and gh issue comment\"")]
    [InlineData("git commit -m \"gh pr comment is refused now\"")]
    [InlineData("git commit -m 'gh pr review is a hole'")]
    [InlineData("git commit -m \"fix: refuse addComment, updateIssueComment and deleteIssueComment\"")]
    [InlineData("git commit -F - <<'EOF'\nfeat: route gh pr comment through h9k pr reply\n\ngh issue comment too\nEOF")]
    [InlineData("echo \"never run gh pr comment here\"")]
    [InlineData("grep -rn 'gh pr comment' docs # gh pr comment")]
    [InlineData("git log --grep='gh pr review'")]
    public void A_read_or_text_that_only_names_a_route_runs(string command)
    {
        ReviewThreadReplyRoutes.WritesIntoAReviewThread(command).Should().BeFalse();
        PullRequestReplyGuardCommand.Denies(Payload("Bash", command)).Should().BeFalse();
        PullRequestReplyGuardCommand.Denies(Payload("PowerShell", command)).Should().BeFalse();
    }

    /// <summary>
    /// The shell reader runs on every shell call of every headless session, over text it does not
    /// control, so it must never throw on any shape of quoting: an unterminated quote, an unclosed
    /// substitution, a dangling backslash, a heredoc with no terminator. A throw would fail the hook
    /// on every such call. A fixed seed keeps the sweep reproducible.
    /// </summary>
    [Fact]
    public void Malformed_quoting_never_throws()
    {
        const string alphabet = "gh pr comment issue review'\"`$()<>{}|&;#\\\n\t-=EOF";
        Random random = new(20261008);
        for (int attempt = 0; attempt < 5000; attempt++)
        {
            string command = string.Create(
                random.Next(1, 60), random, (span, source) =>
                {
                    for (int index = 0; index < span.Length; index++)
                    {
                        span[index] = alphabet[source.Next(alphabet.Length)];
                    }
                });

            Action act = () => ReviewThreadReplyRoutes.WritesIntoAReviewThread(command);

            act.Should().NotThrow($"the command text was {command}");
        }

        foreach (string command in (string[])
            ["gh pr comment '", "echo \"$(", "echo `", "cat <<", "cat <<EOF\nbody", "bash -c", "\\", "$(", "<<-"])
        {
            Action act = () => ReviewThreadReplyRoutes.WritesIntoAReviewThread(command);
            act.Should().NotThrow($"the command text was {command}");
        }
    }

    /// <summary>
    /// The refusal is what the session reads, so it has to name the review-body form: an agent
    /// told only what it may not do retries the same command.
    /// </summary>
    [Fact]
    public void The_refusal_names_the_review_body_form()
    {
        ReviewThreadReplyRoutes.RefusalReason.Should().Contain("h9k pr reply");
        ReviewThreadReplyRoutes.RefusalReason.Should().Contain("--review");
        ReviewThreadReplyRoutes.RefusalReason.Should().Contain("--thread");
    }

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
    /// The second shell, and the reason the matcher is not Bash alone: on this platform's own
    /// primary host a dispatched session is handed a PowerShell tool beside its Bash one, and the
    /// same <c>gh api</c> line runs in either. A Bash-only answer left the ordinary Windows shell
    /// as an open route to every refused endpoint (independent pre-PR review, cycle 1,
    /// conformance and adversarial lenses).
    /// </summary>
    [Theory]
    [InlineData("gh api \"repos/a/b/pulls/7/comments/1/replies\" -f body=x")]
    [InlineData("gh pr review 7 --comment --body 'x'")]
    public void The_hook_denies_the_same_post_through_the_powershell_tool(string command) =>
        PullRequestReplyGuardCommand.Denies(Payload("PowerShell", command)).Should().BeTrue();

    [Fact]
    public void The_hook_allows_a_powershell_call_that_reads() =>
        PullRequestReplyGuardCommand.Denies(Payload("PowerShell", "gh pr view 7 --json reviews"))
            .Should().BeFalse();

    /// <summary>
    /// Every tool named in the matcher is answered for, and nothing in the matcher is left to a
    /// hook that fires and always allows: the command derives its list from the matcher itself.
    /// </summary>
    [Fact]
    public void Every_tool_the_matcher_names_is_answered_for()
    {
        string[] matched = ClaudeSettingsFile.ReviewThreadReplyGuardMatcher.Split('|');
        matched.Should().HaveCountGreaterThan(1);
        foreach (string tool in matched)
        {
            PullRequestReplyGuardCommand.Denies(Payload(
                tool, "gh api \"repos/a/b/pulls/7/comments/1/replies\" -f body=x"))
                .Should().BeTrue($"the hook is registered for {tool}");
        }
    }

    /// <summary>
    /// The matcher names the two shells, but a settings file an operator edited could say
    /// otherwise, and a guard reading another tool's input as a shell command would refuse on
    /// text it cannot interpret.
    /// </summary>
    [Theory]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("WebFetch")]
    public void A_call_to_a_tool_that_is_not_a_shell_is_never_refused(string tool) =>
        PullRequestReplyGuardCommand.Denies(Payload(
            tool, "gh api \"repos/a/b/pulls/7/comments/1/replies\"")).Should().BeFalse();

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
    /// The settings builder installs the hook only when asked, so the one caller that must not
    /// carry it (the interactive claim) is byte-for-byte what it always was.
    /// </summary>
    [Fact]
    public void The_hook_is_written_only_when_the_caller_asks_for_it()
    {
        ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)).Should().NotContain("reply-guard");
        string guarded = ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30), guardReviewThreadReplies: true);
        guarded.Should().Contain("\"PreToolUse\"").And.Contain("h9k pr reply-guard");
        using JsonDocument settings = JsonDocument.Parse(guarded);
        JsonElement preToolUse = settings.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        preToolUse.GetArrayLength().Should().Be(
            1, "a settings file Claude Code cannot parse installs no guard at all");
        preToolUse[0].GetProperty("matcher").GetString().Should().Be(
            ClaudeSettingsFile.ReviewThreadReplyGuardMatcher,
            "a shell the matcher misses is a shell the guard never sees");
    }

    /// <summary>
    /// Every headless launch carries the hook, not only follow-ups (task: a dispatched session
    /// never speaks to a person at the top level of a pull request on its own): the CLI's own
    /// <c>h9k task start</c> and <c>h9k task delegate</c> write what this returns. The interactive
    /// <c>h9k task work</c> session, where the operator is present, does not.
    /// </summary>
    [Fact]
    public void Headless_cli_launches_carry_the_hook_and_the_interactive_claim_does_not()
    {
        HeadlessLaunch.SettingsContent(AgentEffort.High).Should().Contain("h9k pr reply-guard");
        using JsonDocument headless = JsonDocument.Parse(HeadlessLaunch.SettingsContent(AgentEffort.High));
        headless.RootElement.GetProperty("effortLevel").GetString().Should().Be("high");
        headless.RootElement.GetProperty("hooks").GetProperty("PreToolUse").GetArrayLength().Should().Be(1);

        TaskWorkCommand.SettingsContent().Should().NotContain("reply-guard").And.NotContain("hooks");
    }

    private static string Payload(string tool, string command) => JsonSerializer.Serialize(new
    {
        hook_event_name = "PreToolUse",
        tool_name = tool,
        tool_input = new { command },
    });
}
