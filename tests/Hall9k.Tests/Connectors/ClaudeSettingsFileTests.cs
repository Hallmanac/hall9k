using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Pins the one settings-file shape every dispatched session and every interactive
/// <c>h9k task work</c> claim launches with (<c>ClaudeExecutor</c> builds it from the live
/// <c>DaemonOptions.VerifyGateTimeout</c>; <c>TaskWorkCommand</c>, unable to reach that option
/// at all, builds it from <see cref="ClaudeSettingsFile.DefaultCommandTimeout"/>): the
/// co-authored-by suppression (PLAN.md §6.6) and, since the 2026-09-01 finding, the
/// command-timeout headroom that lets a foreground gate run survive without a session needing to
/// know a timeout trick.
/// </summary>
public sealed class ClaudeSettingsFileTests
{
    [Fact]
    public void The_built_content_is_well_formed_json()
    {
        JsonDocument.Parse(ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout)).Dispose();
    }

    [Fact]
    public void The_built_content_suppresses_co_authored_by()
    {
        using JsonDocument document =
            JsonDocument.Parse(ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout));

        document.RootElement.GetProperty("includeCoAuthoredBy").GetBoolean().Should()
            .BeFalse("agents never author co-authored-by trailers (PLAN.md §6.6)");
    }

    [Fact]
    public void The_default_command_timeout_matches_the_platforms_own_gate_timeout_default()
    {
        // ClaudeSettingsFile.DefaultCommandTimeout is the fallback a caller with no live-configured
        // ceiling in reach (TaskWorkCommand, which cannot reference Hall9k.Daemon at all) falls back
        // to — it mirrors DaemonOptions.VerifyGateTimeout's own default. Hall9k.Daemon does
        // reference Hall9k.Connectors, so collapsing the two into one constant is possible in that
        // direction; it is declined deliberately (the gate ceiling should not be defined by a
        // Claude Code settings type), which is what leaves this test as the thing holding them
        // equal.
        ClaudeSettingsFile.DefaultCommandTimeout.Should().Be(new DaemonOptions().VerifyGateTimeout,
            "TaskWorkCommand builds its settings file from this default when VerifyGateTimeout's " +
            "live value is out of reach; a drift here silently reopens the 2026-09-01 finding for " +
            "an interactive h9k task work claim on any machine that never overrode the option");
    }

    [Fact]
    public void Build_sizes_the_default_command_timeout_to_the_requested_value()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)));

        // Read out of the shipped content rather than restated as a second literal beside it, so
        // the assertion cannot agree with a number no session ever receives.
        TimeSpan defaultCommandTimeout = ReadTimeout(document, "BASH_DEFAULT_TIMEOUT_MS");

        defaultCommandTimeout.Should().Be(TimeSpan.FromMinutes(30),
            "a caller with a live-configured ceiling in reach (ClaudeExecutor, resolving " +
            "IOptions<DaemonOptions>.Value.VerifyGateTimeout) must have that value land verbatim " +
            "in the settings file a session actually launches with, not a build-time default " +
            "(2026-09-02 finding: a compile-time constant went stale the moment an operator raised " +
            "the option it claimed to mirror)");
    }

    [Fact]
    public void The_fallback_default_command_timeout_clears_the_stock_two_minute_default()
    {
        using JsonDocument document =
            JsonDocument.Parse(ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout));

        // Build itself enforces no floor — a caller can hand it any TimeSpan, including one
        // below the stock default. This only pins that the fallback DefaultCommandTimeout,
        // the value a caller with no live-configured ceiling in reach actually ships, clears it.
        ReadTimeout(document, "BASH_DEFAULT_TIMEOUT_MS").Should().BeGreaterThan(TimeSpan.FromMinutes(2),
            "the stock 2-minute default killed obedient foreground suite runs (2026-09-01 finding)");
    }

    [Fact]
    public void The_maximum_command_timeout_is_double_whatever_default_was_requested()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)));

        // Double the requested default, so a session's own explicit per-command timeout can still
        // ask for more than the default on a day the suite runs long, whatever the default is.
        ReadTimeout(document, "BASH_MAX_TIMEOUT_MS").Should().Be(TimeSpan.FromMinutes(60),
            "the stock 10-minute cap made foreground compliance impossible on days the suite exceeded it");
    }

    [Fact]
    public void Without_an_effort_the_file_is_byte_for_byte_what_it_was_before_the_knob_existed()
    {
        string expected =
            "{\"includeCoAuthoredBy\": false, \"env\": {\"BASH_DEFAULT_TIMEOUT_MS\": \"1800000\", "
            + "\"BASH_MAX_TIMEOUT_MS\": \"3600000\"}}";

        ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30)).Should().Be(expected);
        ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30), effort: null).Should().Be(expected);
        ClaudeSettingsFile.Build(TimeSpan.FromMinutes(30), effort: AgentEffort.Unknown).Should().Be(expected);
    }

    [Fact]
    public void Every_accepted_effort_is_written_as_effort_level_and_keeps_the_file_well_formed()
    {
        foreach (AgentEffort effort in AgentEffort.All)
        {
            using JsonDocument plain = JsonDocument.Parse(
                ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout, effort: effort));
            using JsonDocument guarded = JsonDocument.Parse(ClaudeSettingsFile.Build(
                ClaudeSettingsFile.DefaultCommandTimeout, guardReviewThreadReplies: true, effort: effort));

            plain.RootElement.GetProperty("effortLevel").GetString().Should().Be(effort.Value);
            guarded.RootElement.GetProperty("effortLevel").GetString().Should().Be(effort.Value);
            guarded.RootElement.TryGetProperty("hooks", out _).Should().BeTrue("the guard hook is unaffected");
            plain.RootElement.GetProperty("env").TryGetProperty("CLAUDE_CODE_EFFORT_LEVEL", out _).Should().BeFalse(
                "the env variable hard-locks the level so a session cannot lower it; the settings key is preferred");
        }
    }

    /// <summary>
    /// Reads one of the settings file's own command-timeout values, so an assertion measures what
    /// a session actually receives instead of a literal restated beside it.
    /// </summary>
    private static TimeSpan ReadTimeout(JsonDocument document, string variable)
    {
        JsonElement value = document.RootElement.GetProperty("env").GetProperty(variable);
        value.ValueKind.Should().Be(JsonValueKind.String,
            "Claude Code parses a settings file's env values as strings");

        return TimeSpan.FromMilliseconds(int.Parse(value.ToString(), CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Pins <see cref="ClaudeSettingsFile.BuildForPrReview"/>'s own shape (security review idea
/// 6be68ee2, process-injection finding 1): the real permission file every pr-review session, its
/// mention follow-up, and every follow-on persona session now launches under, in place of
/// <c>--dangerously-skip-permissions</c>. Every claim pinned here was verified empirically against
/// Claude Code 2.1.283 during the 2026-09-27 challenge (this task's own journal.md, verdict C1).
/// </summary>
public sealed class ClaudeSettingsFileBuildForPrReviewTests
{
    [Fact]
    public void The_default_mode_is_dont_ask()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        document.RootElement.GetProperty("permissions").GetProperty("defaultMode").GetString().Should().Be("dontAsk",
            "Claude Code only reads the mode from permissions.defaultMode — a top-level key is silently " +
            "ignored, and the owner's own user settings carry defaultMode: acceptEdits, still loaded under " +
            "--setting-sources user, so a session ran under acceptEdits and a Write call created a file " +
            "(verified: four throwaway claude -p probes)");
    }

    [Fact]
    public void Read_and_grep_are_scoped_to_the_checkout_and_the_run_directory()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/pr-review-checkout", "/tmp/pr-review-run"));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().Contain("Read(//tmp/pr-review-checkout/**)",
            "a single leading slash is relative to the settings root, not the filesystem root — only " +
            "the doubled slash Claude Code's own docs use (Edit(//etc/*)) is absolute, and an unscoped " +
            "Read can otherwise reach ~/.config/gh/hosts.yml (fold-in fix, this task's journal.md)");
        allow.Should().Contain("Read(//tmp/pr-review-run/**)");
        allow.Should().Contain("Grep(//tmp/pr-review-checkout/**)",
            "Grep reads matching lines of file content, not only file names, so it needs the identical " +
            "scoping Read gets rather than being left unlimited beside Glob");
        allow.Should().Contain("Grep(//tmp/pr-review-run/**)");
    }

    /// <summary>
    /// Pins <c>NormalizeForPermissionRule</c>'s own behavior (independent pre-PR review, cycle 2,
    /// conformance lens): a drive-letter backslash path must land in the forward-slash,
    /// lowercase-drive form Claude Code actually matches on a Windows node (lesson ccc37c9c),
    /// not the native backslash form every other test here builds from a POSIX path.
    /// </summary>
    [Fact]
    public void A_windows_style_path_is_normalized_to_claude_codes_own_posix_form()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), @"C:\Users\owner\checkout", @"C:\Users\owner\run"));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().Contain("Read(//c/Users/owner/checkout/**)",
            "a drive letter must be lowercased and the backslashes turned to forward slashes before " +
            "the doubled-leading-slash form is built around it, or the rule silently matches nothing " +
            "on a Windows node");
        allow.Should().Contain("Grep(//c/Users/owner/checkout/**)");
        allow.Should().Contain("Read(//c/Users/owner/run/**)");
        allow.Should().Contain("Write(//c/Users/owner/run/mention-answer.md)",
            "the mention-answer write rule is built from the same normalization, not just the Read/Grep rules");
    }

    [Fact]
    public void The_allow_list_carries_a_write_rule_for_the_mention_answer_file()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().Contain("Write(//tmp/run/mention-answer.md)",
            "a mention-minted primary session's own prompt (MentionFollowUpPromptBuilder.BuildMintAddendum) " +
            "asks it to write this exact file, and with no allow rule the write is refused under dontAsk");
    }

    [Fact]
    public void The_allow_list_carries_exactly_what_the_lenses_actually_run()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().Contain(["Glob", "Bash(git diff:*)", "Bash(git log:*)", "Bash(gh pr view:*)",
            "Bash(gh pr diff:*)", "Bash(gh pr checks:*)", "Bash(gh issue view:*)"]);
        allow.Should().NotContain(rule => rule.Contains("gh api", StringComparison.Ordinal),
            "gh api is refused by the deny list even for a read; deny beats allow");
    }

    [Fact]
    public void The_deny_list_keeps_the_review_laps_own_list_and_adds_a_claude_denial()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] deny = [.. document.RootElement.GetProperty("permissions").GetProperty("deny")
            .EnumerateArray().Select(element => element.GetString()!)];

        deny.Should().Contain(ClaudeSettingsFile.ReviewLapDeniedTools);
        deny.Should().Contain("Bash(gh api:*)");
        deny.Should().Contain("Bash(claude:*)",
            "the owner's own user settings allow claude:*, and a flag-file deny is the only thing " +
            "that beats a user-level allow under dontAsk (verified: claude --version ran without it)");
    }

    [Fact]
    public void The_reply_guard_hook_is_installed()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        document.RootElement.TryGetProperty("hooks", out _).Should().BeTrue();
    }

    [Fact]
    public void The_git_read_only_guard_hook_is_installed_beside_the_reply_guard()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] matchers = [.. document.RootElement.GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray().Select(element => element.GetProperty("matcher").GetString()!)];

        matchers.Should().HaveCount(2,
            "the reply guard and the git read-only guard live under the one hooks key a settings file " +
            "may hold, so both must be assembled together rather than one splice overwriting the other");
    }

    [Fact]
    public void With_no_qa_gate_commands_the_allow_list_carries_none()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().HaveCount(5 + ClaudeSettingsFile.PrReviewAllowedTools.Count,
            "the two Read rules, the two Grep rules, the mention-answer Write rule, and the fixed tool " +
            "list, and nothing else — QA's own gate commands are the one earned exception, and nothing " +
            "was supplied here");
    }

    [Fact]
    public void Qa_gate_commands_are_allowed_verbatim_on_top_of_the_fixed_list()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run",
            [new VerifyCommand("test", "dotnet test"), new VerifyCommand("build", "dotnet build")]));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().Contain("Bash(dotnet test:*)").And.Contain("Bash(dotnet build:*)",
            "QA's own job is to build and run this project's tests (qa checks.md)");
    }

    [Fact]
    public void The_command_timeout_env_still_carries_through()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(45), "/tmp/checkout", "/tmp/run"));

        document.RootElement.GetProperty("env").GetProperty("BASH_DEFAULT_TIMEOUT_MS").GetString()
            .Should().Be(TimeSpan.FromMinutes(45).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Every pr-review, mention follow-up and follow-on persona session used to get effortLevel
    /// through Build's own effort parameter; BuildForPrReview dropped it entirely, silently
    /// running every one of them at Claude Code's own default effort (independent pre-PR review,
    /// cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public void Every_accepted_effort_is_written_as_effort_level()
    {
        foreach (AgentEffort effort in AgentEffort.All)
        {
            using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
                TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run", effort: effort));

            document.RootElement.GetProperty("effortLevel").GetString().Should().Be(effort.Value);
        }
    }

    [Fact]
    public void With_no_effort_the_effort_level_key_is_left_out()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        document.RootElement.TryGetProperty("effortLevel", out _).Should().BeFalse();
    }
}
