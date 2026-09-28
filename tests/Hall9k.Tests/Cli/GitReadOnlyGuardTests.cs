using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Prompts;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The hook that closes the write-outside-the-checkout hole a pr-review session's own otherwise
/// read-only <c>git diff</c>/<c>git log</c> allowance left open (independent pre-PR review, cycle
/// 1, both lenses): a prefix rule (<c>Bash(git diff:*)</c>) allows the command as spelled, and
/// <c>--output=&lt;path&gt;</c> is still spelled "git diff" while writing formatted content to any
/// path the process can write.
/// </summary>
public sealed class GitReadOnlyGuardTests
{
    [Theory]
    [InlineData("git diff origin/main...HEAD --output=/Users/owner/.zshrc")]
    [InlineData("git diff --output /Users/owner/.zshrc")]
    [InlineData("git log -1 --format='format:echo pwned' --output=/tmp/out.txt")]
    [InlineData("git log --oneline --output=notes.md")]
    public void A_git_diff_or_log_with_an_output_flag_is_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    /// <summary>
    /// Quoting or escaping part of the flag still reassembles into the real flag once the shell
    /// removes the quote or the backslash (independent pre-PR review, cycle 1, adversarial lens;
    /// each spelling verified in a throwaway repository to write its file identically to the
    /// unquoted form).
    /// </summary>
    [Theory]
    [InlineData("git diff \"--output\"=/tmp/q1")]
    [InlineData("git diff --outpu\\t=/tmp/q2")]
    [InlineData("git log -1 --format='format:echo pwned' '--output'=/tmp/q3")]
    public void A_quoted_or_escaped_output_flag_is_still_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    /// <summary>
    /// Either subcommand silently switches to filesystem-diff mode the moment it sees an absolute
    /// path, with no flag naming the mode at all (lesson f059f669) — the read escape that
    /// <c>Bash(git diff:*)</c>'s own prefix rule cannot tell apart from an ordinary diff.
    /// </summary>
    [Theory]
    [InlineData("git diff /dev/null ~/.config/gh/hosts.yml")]
    [InlineData("git diff /dev/null /tmp/pr-review-run/secret.txt")]
    [InlineData("git diff --no-index HEAD ~/.config/gh/hosts.yml")]
    [InlineData("git log --no-index")]
    public void A_git_diff_or_log_that_reads_outside_the_checkout_is_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    [Theory]
    [InlineData("git diff origin/main...HEAD")]
    [InlineData("git log -1 --format=%H")]
    [InlineData("git log --oneline -20")]
    [InlineData("git diff --stat")]
    [InlineData("git diff origin/main...HEAD -- src/Foo.cs")]
    public void An_ordinary_git_diff_or_log_with_no_output_flag_runs(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeFalse();

    /// <summary>Naming the flag in a search or a commit message is not using it.</summary>
    [Theory]
    [InlineData("git grep -- --output src/")]
    [InlineData("git commit -m \"docs: explain git log --output\"")]
    [InlineData("git commit -m \"note: try git diff --no-index someday\"")]
    [InlineData("dotnet test")]
    public void A_command_that_only_names_the_flag_runs(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeFalse();

    [Fact]
    public void The_hook_denies_a_bash_call_with_the_output_flag() =>
        PrReviewGitOutputGuardCommand.Denies(Payload(
            "Bash", "git diff origin/main...HEAD --output=/Users/owner/.zshrc")).Should().BeTrue();

    [Fact]
    public void The_hook_allows_an_ordinary_bash_call() =>
        PrReviewGitOutputGuardCommand.Denies(Payload("Bash", "git diff origin/main...HEAD")).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"tool_name\":\"Bash\"}")]
    public void An_unreadable_payload_lets_the_call_run(string? payload) =>
        PrReviewGitOutputGuardCommand.Denies(payload).Should().BeFalse();

    [Fact]
    public void The_hook_denies_the_same_command_through_the_powershell_tool() =>
        PrReviewGitOutputGuardCommand.Denies(Payload(
            "PowerShell", "git log -1 --output=out.txt")).Should().BeTrue();

    [Theory]
    [InlineData("Write")]
    [InlineData("WebFetch")]
    public void A_call_to_a_tool_that_is_not_a_shell_is_never_refused(string tool) =>
        PrReviewGitOutputGuardCommand.Denies(Payload(tool, "git diff --output=out.txt")).Should().BeFalse();

    [Fact]
    public void The_refusal_survives_serialization()
    {
        using JsonDocument denial = JsonDocument.Parse(PrReviewGitOutputGuardCommand.DenialJson());
        JsonElement output = denial.RootElement.GetProperty("hookSpecificOutput");
        output.GetProperty("hookEventName").GetString().Should().Be("PreToolUse");
        output.GetProperty("permissionDecision").GetString().Should().Be("deny");
        output.GetProperty("permissionDecisionReason").GetString().Should().Be(
            GitReadOnlyGuardRoutes.RefusalReason, "the reason reaches the model verbatim or not at all");
    }

    private static string Payload(string tool, string command) => JsonSerializer.Serialize(new
    {
        hook_event_name = "PreToolUse",
        tool_name = tool,
        tool_input = new { command },
    });
}
