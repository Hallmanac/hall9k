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
/// <c>h9k task drive</c> claim launches with (<c>ClaudeExecutor</c> builds it from the live
/// <c>DaemonOptions.VerifyGateTimeout</c>; <c>TaskDriveCommand</c>, unable to reach that option
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
        // ceiling in reach (TaskDriveCommand, which cannot reference Hall9k.Daemon at all) falls back
        // to — it mirrors DaemonOptions.VerifyGateTimeout's own default. Hall9k.Daemon does
        // reference Hall9k.Connectors, so collapsing the two into one constant is possible in that
        // direction; it is declined deliberately (the gate ceiling should not be defined by a
        // Claude Code settings type), which is what leaves this test as the thing holding them
        // equal.
        ClaudeSettingsFile.DefaultCommandTimeout.Should().Be(new DaemonOptions().VerifyGateTimeout,
            "TaskDriveCommand builds its settings file from this default when VerifyGateTimeout's " +
            "live value is out of reach; a drift here silently reopens the 2026-09-01 finding for " +
            "an interactive h9k task drive claim on any machine that never overrode the option");
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
    /// <summary>
    /// A pr-review checkout is a standalone clone with no remote, so a bare pull request number has no
    /// repository to resolve in unless the environment names it. The session cannot run <c>gh</c> itself, but
    /// the setting is still carried for a process it starts that does.
    /// </summary>
    [Fact]
    public void The_named_repository_is_set_as_gh_repo_so_gh_reads_work_in_a_checkout_with_no_remote()
    {
        using JsonDocument named = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run", gitHubRepository: "owner/repo"));
        using JsonDocument unnamed = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        named.RootElement.GetProperty("env").GetProperty("GH_REPO").GetString().Should().Be("owner/repo");
        unnamed.RootElement.GetProperty("env").TryGetProperty("GH_REPO", out _).Should().BeFalse(
            "a session with no named repository leaves the variable unset rather than setting it blank");
    }

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
        allow.Should().Contain("Edit(//c/Users/owner/run/mention-answer.md)",
            "the mention-answer write rule is built from the same normalization, not just the Read/Grep rules");
    }

    /// <summary>
    /// The feedback evaluator's one extra write (task: before the owner decides on a non-member's review
    /// feedback, a read-only evaluator assesses it): its own assessment file, as a single path rule, and
    /// nothing else added, removed or loosened.
    /// </summary>
    [Fact]
    public void An_assessment_file_adds_exactly_one_write_path_and_nothing_else()
    {
        string[] Rules(string? assessmentFile)
        {
            using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
                TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run", assessmentFile: assessmentFile));
            JsonElement permissions = document.RootElement.GetProperty("permissions");
            return
            [
                .. permissions.GetProperty("allow").EnumerateArray().Select(element => $"allow:{element.GetString()}"),
                .. permissions.GetProperty("deny").EnumerateArray().Select(element => $"deny:{element.GetString()}"),
                $"mode:{permissions.GetProperty("defaultMode").GetString()}",
            ];
        }

        string[] without = Rules(null);
        string[] with = Rules("/tmp/run/feedback-assessment-0123456789ab.md");

        with.Except(without).Should().Equal(["allow:Edit(//tmp/run/feedback-assessment-0123456789ab.md)"]);
        without.Except(with).Should().BeEmpty("no rule is removed to make room for it");
        with.Count(rule => rule.StartsWith("allow:Edit(", StringComparison.Ordinal)).Should().Be(2,
            "mention-answer.md plus the one assessment file, and no directory-wide write");
        with.Should().NotContain(rule => rule.StartsWith("allow:Write(", StringComparison.Ordinal),
            "Claude Code 2.1.316 refuses the Write tool under a Write(...) allow rule and allows it under Edit(...)");
        with.Should().NotContain(rule => rule.Contains("--dangerously-skip-permissions", StringComparison.Ordinal));
    }

    /// <summary>
    /// A draft challenge reads the idea's workspace and the draft's own directory beside the checkout and its own
    /// session directory: each extra root adds one Read and one Grep rule, scoped like the first two, and nothing
    /// else changes.
    /// </summary>
    [Fact]
    public void Extra_read_roots_add_one_read_and_one_grep_rule_each_and_nothing_else()
    {
        string[] Allow(IReadOnlyList<string>? roots)
        {
            using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
                TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run", extraReadRoots: roots));
            return [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
                .EnumerateArray().Select(element => element.GetString()!)];
        }

        string[] without = Allow(null);
        string[] with = Allow(["/tmp/idea/workspace", "/tmp/task"]);

        with.Except(without).Should().BeEquivalentTo(
            ["Read(//tmp/idea/workspace/**)", "Grep(//tmp/idea/workspace/**)", "Read(//tmp/task/**)", "Grep(//tmp/task/**)"]);
        without.Except(with).Should().BeEmpty();
    }

    /// <summary>A draft challenge writes its report and nothing else: no mention-answer write rides beside it.</summary>
    [Fact]
    public void Without_the_mention_answer_the_report_is_the_only_write_rule()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run",
            assessmentFile: "/tmp/run/challenge-report.md", allowMentionAnswer: false));

        string[] writes = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)
            .Where(rule => rule.StartsWith("Edit(", StringComparison.Ordinal) || rule.StartsWith("Write(", StringComparison.Ordinal))];

        writes.Should().Equal(["Edit(//tmp/run/challenge-report.md)"]);
    }

    [Fact]
    public void The_allow_list_carries_a_write_rule_for_the_mention_answer_file()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] allow = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)];

        allow.Should().Contain("Edit(//tmp/run/mention-answer.md)",
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

        allow.Should().Contain(["Glob", "Bash(git diff:*)", "Bash(git log:*)"]);
        allow.Should().NotContain(rule => rule.Contains("gh", StringComparison.Ordinal) && rule.StartsWith("Bash(", StringComparison.Ordinal),
            "a pr-review session reads GitHub only through what the daemon gathered for it");
    }

    [Fact]
    public void The_two_git_prefix_rules_are_the_only_git_rules()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        string[] gitRules = [.. document.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(element => element.GetString()!)
            .Where(rule => rule.StartsWith("Bash(git", StringComparison.Ordinal))];

        gitRules.Should().BeEquivalentTo(["Bash(git diff:*)", "Bash(git log:*)"],
            "Claude Code's own read-only git allowance does not apply in a dispatched worktree, so these two " +
            "cannot be dropped; every other git command is refused, and a prefix rule for one would turn off " +
            "Claude Code's own flag checks for it with nothing of ours looking at its flags");
    }

    [Fact]
    public void A_session_without_gate_commands_keeps_git_to_the_checkouts_own_repository()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run"));

        JsonElement env = document.RootElement.GetProperty("env");

        env.GetProperty("GIT_CONFIG_COUNT").GetString().Should().Be("1");
        env.GetProperty("GIT_CONFIG_KEY_0").GetString().Should().Be("safe.bareRepository");
        env.GetProperty("GIT_CONFIG_VALUE_0").GetString().Should().Be("explicit");
    }

    [Fact]
    public void A_session_with_gate_commands_is_left_without_the_bare_repository_setting()
    {
        using JsonDocument document = JsonDocument.Parse(ClaudeSettingsFile.BuildForPrReview(
            TimeSpan.FromMinutes(30), "/tmp/checkout", "/tmp/run", [new VerifyCommand("test", "dotnet test")]));

        document.RootElement.GetProperty("env").TryGetProperty("GIT_CONFIG_COUNT", out _).Should().BeFalse(
            "the project's own gates create bare repositories on purpose, so the setting would fail QA's run");
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
        deny.Should().Contain("Bash(gh:*)", "every gh command is refused, reads included");
        deny.Should().Contain("PowerShell(gh:*)", "the PowerShell tool is a live shell here too, as the hooks' own matcher treats it");
        ClaudeSettingsFile.PrReviewDeniedTools.Should().BeEquivalentTo(
            [
                .. ClaudeSettingsFile.ReviewLapDeniedTools, "Bash(gh:*)", "PowerShell(gh:*)", "Bash(claude:*)",
                "RemoteTrigger", "PushNotification", "DesignSync", "Workflow",
                "CronCreate", "CronDelete", "SendToProject", "SendMessage",
            ]);
        deny.Should().Contain(
            ["RemoteTrigger", "PushNotification", "DesignSync", "Workflow", "CronCreate", "CronDelete", "SendToProject", "SendMessage"],
            "a review session reads what the daemon gathered and has no use for a tool that reaches outside the review");

        // The human review lap shares this list and keeps its gh reads, which is why the blanket gh denial lives
        // in the pr-review list and not here: pinned so it cannot drift into the lap's own.
        ClaudeSettingsFile.ReviewLapDeniedTools.Should().Equal(
            "Bash(git push:*)",
            "Bash(gh pr create:*)", "Bash(gh pr review:*)", "Bash(gh pr comment:*)", "Bash(gh pr edit:*)",
            "Bash(gh pr merge:*)", "Bash(gh pr close:*)", "Bash(gh pr reopen:*)", "Bash(gh pr ready:*)",
            "Bash(gh pr lock:*)", "Bash(gh pr unlock:*)", "Bash(gh pr revert:*)", "Bash(gh pr update-branch:*)",
            "Bash(gh issue create:*)", "Bash(gh issue comment:*)", "Bash(gh issue edit:*)",
            "Bash(gh issue close:*)", "Bash(gh issue reopen:*)", "Bash(gh issue lock:*)",
            "Bash(gh issue unlock:*)", "Bash(gh issue delete:*)", "Bash(gh issue transfer:*)",
            "Bash(gh issue pin:*)", "Bash(gh issue unpin:*)", "Bash(gh issue develop:*)",
            "Bash(gh api:*)", "Bash(h9k pr approve:*)", "Bash(h9k pr request-changes:*)");
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
