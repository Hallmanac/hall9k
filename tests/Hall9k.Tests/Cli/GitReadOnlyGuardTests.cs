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
    /// Bash's own <c>$'...'</c> ANSI-C quoting decodes backslash escapes before the shell ever sees
    /// a flag or a path, defeating the plain <c>'...'</c>/<c>"..."</c> and backslash handling above
    /// the identical way (independent pre-PR review, cycle 2, adversarial lens). Both spellings
    /// verified against bash directly: <c>bash -c "echo --outp\$'\x75't=/tmp/x"</c> prints
    /// <c>--output=/tmp/x</c>, and <c>bash -c "echo git diff HEAD \$'/etc/passwd'"</c> prints
    /// <c>git diff HEAD /etc/passwd</c>.
    /// </summary>
    [Theory]
    [InlineData("git diff --outp$'\\x75't=/tmp/x")]
    [InlineData("git log -1 --format=%H --outp$'\\x75t'=/tmp/y")]
    public void An_ansi_c_quoted_output_flag_is_still_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    /// <summary>
    /// The read-side half of the same gap: <c>$'...'</c> quoting an absolute path reassembles into
    /// the identical <c>--no-index</c>-triggering argument once bash decodes it, with no separating
    /// whitespace left for the plain-quote handling above to notice (independent pre-PR review,
    /// cycle 2, adversarial lens).
    /// </summary>
    [Theory]
    [InlineData("git diff HEAD $'/etc/passwd'")]
    [InlineData("git diff $'/dev/null' $'/etc/passwd'")]
    public void An_ansi_c_quoted_absolute_path_is_still_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    /// <summary>
    /// The identical whitespace-blanking heuristic the plain-quote branch pins (naming the flag or
    /// an absolute path inside an ordinary multi-word argument must keep running, see
    /// <see cref="A_command_that_only_names_the_flag_runs"/>) applies to <c>$'...'</c> too: an
    /// ordinary <c>--format</c> string spelled with ANSI-C quoting that merely names
    /// <c>--output</c> inside a multi-word value is not using the flag.
    /// </summary>
    [Theory]
    [InlineData("git log -1 --format=$'note: mentions --output here'")]
    [InlineData("git log -1 --format=$'note: try /etc/passwd someday'")]
    public void An_ansi_c_quoted_multi_word_argument_that_only_names_a_refused_term_runs(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeFalse();

    /// <summary>
    /// The write escape the cycle 3 adversarial verify pass reopened: when the whole
    /// <c>--output=&lt;path&gt;</c> flag, not just the path, sits inside one <c>$'...'</c> span and
    /// the decoded path itself contains a space or a tab (an ordinary real-world path, e.g.
    /// <c>/tmp/my file.txt</c>), blanking the entire span on whitespace alone erased the
    /// <c>--output=</c> prefix along with the value, so the flag was never seen. Verified against
    /// bash directly: <c>git diff $'--output=/tmp/my file.txt'</c> is one argv token and git's own
    /// <c>--flag=value</c> parsing takes everything after <c>=</c> as the value regardless of
    /// embedded whitespace.
    /// </summary>
    [Theory]
    [InlineData("git diff $'--output=/tmp/my file.txt'")]
    [InlineData("git log -1 --format=%H $'--output=/tmp/my file.txt'")]
    [InlineData("git diff $'--no-index /tmp/a b'")]
    public void An_ansi_c_quoted_flag_and_whitespace_bearing_value_together_is_still_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    /// <summary>
    /// The whole quoting class cycle 4 human review found live on the pushed head, verified against
    /// bash and git 2.55: each of these is one shell word once bash removes the quoting, and each
    /// one really writes or reads the named file. A flag split across a plain quote boundary
    /// (<c>--out'put=...'</c>, <c>--out"put=...'</c>) or wrapped whole in a plain quote that also
    /// carries a space (<c>'--output=/tmp/my file.txt'</c>, and the double-quoted form) was never
    /// checked against <see cref="GitReadOnlyGuardRoutes"/>'s per-word start-of-word rule before —
    /// only the <c>$'...'</c> branch had one. <c>--outp$'ut=/tmp/my file.txt'</c> defeated even that
    /// branch's own check, because it looked only at the decoded <c>$'...'</c> span
    /// (<c>ut=/tmp/my file.txt</c>, which does not start with <c>--output</c>) rather than the whole
    /// word the unquoted <c>--outp</c> prefix and the span reassemble into. The last case is an
    /// implicit <c>--no-index</c> read of two quoted absolute paths with no flag at all.
    /// </summary>
    [Theory]
    [InlineData("git diff '--output=/tmp/my file.txt'")]
    [InlineData("git diff \"--output=/tmp/my file.txt\"")]
    [InlineData("git diff --out'put=/tmp/my file.txt'")]
    [InlineData("git diff --out\"put=$HOME/my file\"")]
    [InlineData("git diff --outp$'ut=/tmp/my file.txt'")]
    [InlineData("git diff '/Users/b/App Support/a' '/Users/b/App Support/b'")]
    public void A_shell_word_reassembled_from_any_quoting_that_starts_with_a_refused_term_is_refused(string command) =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout(command).Should().BeTrue();

    /// <summary>
    /// The other half of cycle 4's fix: reassembling plain-quoted words the same way as
    /// <c>$'...'</c> ones must not start refusing an ordinary plain-quoted multi-word value that
    /// never itself opens with a refused term, the same guarantee
    /// <see cref="A_command_that_only_names_the_flag_runs"/> already pins for an unquoted mention.
    /// </summary>
    [Fact]
    public void A_plain_quoted_format_string_runs() =>
        GitReadOnlyGuardRoutes.EscapesTheCheckout("git log -1 --format='format:%H %s'").Should().BeFalse();

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
