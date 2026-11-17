using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Orchestrator;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Connection;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The two paste handoffs, <c>h9k task drive --print-prompt</c> and <c>h9k pr review</c>, name a bound project's
/// Claude Code account and print a start command carrying it, in the host shell's form, raw on a line of its own.
/// An unbound project's handoff is pinned to the text it printed before the account existed (commit 1fdfbf66).
/// </summary>
public sealed class PasteHandoffAccountTests
{
    private static readonly Guid TaskId = Guid.Parse("01a1ea5a-ef53-77ad-89bf-36b5707917d1");
    private static readonly Guid ConnectionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private const string PosixDirectory = "/Users/me/it's/.claude-alt";
    private const string PosixSettings = "/Users/me/my home/.hall9k/runs/r1/settings.json";
    private const string PowerShellDirectory = "C:\\Users\\me\\it's\\.claude-alt";
    private const string PowerShellSettings = "C:\\Users\\me\\my home\\.hall9k\\runs\\r1\\settings.json";

    private const string PosixStart =
        "CLAUDE_CONFIG_DIR='/Users/me/it'\\''s/.claude-alt' claude --settings \"/Users/me/my home/.hall9k/runs/r1/settings.json\"";

    private const string PowerShellStart =
        "try { $env:CLAUDE_CONFIG_DIR='C:\\Users\\me\\it''s\\.claude-alt'; claude --settings \"C:\\Users\\me\\my home\\.hall9k\\runs\\r1\\settings.json\" } "
        + "finally { Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction Ignore }";

    private static readonly ClaudeCodeAccount PosixAccount = new(ConnectionId, PosixDirectory, "dev@example.com", "org-1");
    private static readonly ClaudeCodeAccount PowerShellAccount = new(ConnectionId, PowerShellDirectory, null, null);

    [Fact]
    public void Task_drive_names_a_bound_account_and_prints_its_start_command_in_posix()
    {
        string output = TaskDrive(PosixSettings, PosixAccount, LaunchShell.Posix);

        output.Should().Be(Lines(
            "Worktree: /w",
            "Branch: task/b",
            $"This project runs under its bound Claude Code account, dev@example.com ({PosixDirectory}). Start your session with the command below, which sets that account for it and passes the settings file:",
            PosixStart,
            $"Settings file (recommended): {PosixSettings}. The command above passes it with --settings, for this platform's required conventions (no co-authored-by trailers, longer command-tool timeouts).",
            "Paste the prompt below into the Claude Code session that command starts, in its own terminal or in this one, wherever suits you:",
            "",
            "the prompt"));
    }

    [Fact]
    public void Task_drive_names_a_bound_account_and_prints_its_start_command_in_powershell()
    {
        string output = TaskDrive(PowerShellSettings, PowerShellAccount, LaunchShell.PowerShell);

        output.Should().Be(Lines(
            "Worktree: /w",
            "Branch: task/b",
            $"This project runs under its bound Claude Code account, an unobserved account ({PowerShellDirectory}). Start your session with the command below, which sets that account for it and passes the settings file:",
            PowerShellStart,
            $"Settings file (recommended): {PowerShellSettings}. The command above passes it with --settings, for this platform's required conventions (no co-authored-by trailers, longer command-tool timeouts).",
            "Paste the prompt below into the Claude Code session that command starts, in its own terminal or in this one, wherever suits you:",
            "",
            "the prompt"));
    }

    [Theory]
    [InlineData(LaunchShell.Posix)]
    [InlineData(LaunchShell.PowerShell)]
    public void Task_drive_for_an_unbound_project_prints_what_it_printed_before_the_account_existed(LaunchShell shell)
    {
        string output = TaskDrive(PosixSettings, ClaudeCodeAccount.Default, shell);

        output.Should().Be(Lines(
            "Worktree: /w",
            "Branch: task/b",
            $"Settings file (recommended): {PosixSettings} — pass --settings {PosixSettings} to your own claude invocation for this platform's required conventions (no co-authored-by trailers, longer command-tool timeouts).",
            "Paste the prompt below into a Claude Code session started anywhere — its own terminal, this one once you exit h9k, wherever suits you:",
            "",
            "the prompt"));
    }

    [Theory]
    [InlineData(ReviewLapGuardOutcome.AlreadyPresent)]
    [InlineData(ReviewLapGuardOutcome.Failed)]
    [InlineData(ReviewLapGuardOutcome.NoWorktree)]
    public void Pr_review_names_a_bound_account_and_points_every_settings_line_at_its_start_command_in_posix(ReviewLapGuardOutcome guard)
    {
        string worktree = guard == ReviewLapGuardOutcome.NoWorktree ? string.Empty : "/checkout";
        string output = PullRequestReview(worktree, PosixSettings, guard, PosixAccount, LaunchShell.Posix);

        output.Should().Be(Lines(
            CheckoutLine(worktree),
            $"This project runs under its bound Claude Code account, dev@example.com ({PosixDirectory}). Start your session with the command below, which sets that account for it and passes the settings file:",
            PosixStart,
            BoundGuardLine(guard, PosixSettings),
            $"Settings file: {PosixSettings}. The command above passes it with --settings, for this platform's required conventions and the same push guard.",
            LapEndsLine,
            "Paste the briefing below into the Claude Code session that command starts. The checkout above is the natural place to start it, but nothing requires it:",
            "",
            "the briefing"));
    }

    [Theory]
    [InlineData(ReviewLapGuardOutcome.AlreadyPresent)]
    [InlineData(ReviewLapGuardOutcome.Failed)]
    [InlineData(ReviewLapGuardOutcome.NoWorktree)]
    public void Pr_review_names_a_bound_account_and_points_every_settings_line_at_its_start_command_in_powershell(ReviewLapGuardOutcome guard)
    {
        string worktree = guard == ReviewLapGuardOutcome.NoWorktree ? string.Empty : "C:\\checkout";
        string output = PullRequestReview(worktree, PowerShellSettings, guard, PowerShellAccount, LaunchShell.PowerShell);

        output.Should().Be(Lines(
            CheckoutLine(worktree),
            $"This project runs under its bound Claude Code account, an unobserved account ({PowerShellDirectory}). Start your session with the command below, which sets that account for it and passes the settings file:",
            PowerShellStart,
            BoundGuardLine(guard, PowerShellSettings),
            $"Settings file: {PowerShellSettings}. The command above passes it with --settings, for this platform's required conventions and the same push guard.",
            LapEndsLine,
            "Paste the briefing below into the Claude Code session that command starts. The checkout above is the natural place to start it, but nothing requires it:",
            "",
            "the briefing"));
    }

    [Theory]
    [InlineData(ReviewLapGuardOutcome.Written)]
    [InlineData(ReviewLapGuardOutcome.AlreadyGuarded)]
    public void Pr_review_keeps_the_guard_lines_that_name_no_settings_flag_for_a_bound_project(ReviewLapGuardOutcome guard)
    {
        string output = PullRequestReview("/checkout", PosixSettings, guard, PosixAccount, LaunchShell.Posix);

        output.Should().Contain(PosixStart + "\n" + UnboundGuardLine(guard, "/checkout", PosixSettings) + "\n");
    }

    [Theory]
    [InlineData(ReviewLapGuardOutcome.Written)]
    [InlineData(ReviewLapGuardOutcome.AlreadyGuarded)]
    [InlineData(ReviewLapGuardOutcome.AlreadyPresent)]
    [InlineData(ReviewLapGuardOutcome.Failed)]
    [InlineData(ReviewLapGuardOutcome.NoWorktree)]
    public void Pr_review_for_an_unbound_project_prints_what_it_printed_before_the_account_existed(ReviewLapGuardOutcome guard)
    {
        string worktree = guard == ReviewLapGuardOutcome.NoWorktree ? string.Empty : "/checkout";
        string output = PullRequestReview(worktree, PosixSettings, guard, ClaudeCodeAccount.Default, LaunchShell.Posix);

        output.Should().Be(Lines(
            CheckoutLine(worktree),
            UnboundGuardLine(guard, worktree, PosixSettings),
            $"Settings file: {PosixSettings} — pass --settings {PosixSettings} to your own claude invocation for this platform's required conventions and the same push guard.",
            LapEndsLine,
            "Paste the briefing below into a Claude Code session started anywhere — the checkout above is the natural place, but nothing requires it:",
            "",
            "the briefing"));
    }

    private static string LapEndsLine =>
        $"This lap ends only when you run h9k pr approve {TaskId} --note \"…\" or h9k pr request-changes {TaskId} --note \"…\" — never on its own.";

    private static string CheckoutLine(string worktree) => worktree.Length > 0
        ? $"Read-only checkout (a standalone clone): {worktree}"
        : "No worktree (--no-worktree) — review against your deployed environment.";

    private static string BoundGuardLine(ReviewLapGuardOutcome guard, string settings) => guard switch
    {
        ReviewLapGuardOutcome.AlreadyPresent =>
            $"The checkout already had a {ReviewLapGuardFile.RelativePath}, so the push guard was NOT written there and nothing of the pull request's own was overwritten. Start your session with the command above, which passes --settings {settings} and so carries the same denials.",
        ReviewLapGuardOutcome.Failed =>
            $"The push guard could not be written into the checkout. Start your session with the command above, which passes --settings {settings} and so carries the same denials.",
        _ =>
            $"No worktree, so the push guard travels with the settings file: start your session with the command above, which passes --settings {settings}.",
    };

    /// <summary>The guard line at commit 1fdfbf66, written out rather than read from the code under test.</summary>
    private static string UnboundGuardLine(ReviewLapGuardOutcome guard, string worktree, string settings) => guard switch
    {
        ReviewLapGuardOutcome.Written =>
            $"Push guard installed at {ReviewLapGuardFile.PathIn(worktree)} — any Claude Code session started in that checkout is denied git push, every gh write verb, gh api (the endpoint they all reach), and your own two verdict commands, with no flag needed.",
        ReviewLapGuardOutcome.AlreadyGuarded =>
            $"Push guard already at {ReviewLapGuardFile.PathIn(worktree)} from an earlier entry into this lap, unchanged — any Claude Code session started in that checkout is still covered by it, with no flag needed.",
        ReviewLapGuardOutcome.AlreadyPresent =>
            $"The checkout already had a {ReviewLapGuardFile.RelativePath}, so the push guard was NOT written there — nothing of the pull request's own was overwritten. Start your session with --settings {settings}, which carries the same denials.",
        ReviewLapGuardOutcome.Failed =>
            $"The push guard could not be written into the checkout. Start your session with --settings {settings}, which carries the same denials.",
        _ =>
            $"No worktree, so the push guard travels with the settings file: start your session with --settings {settings}.",
    };

    private static string TaskDrive(string settings, ClaudeCodeAccount account, LaunchShell shell) =>
        Normalized(ScopedAnsiConsoleCapture.Capture(() => TaskDriveCommand.PrintPromptHandoff(
            TaskId, "/w", "task/b", "the prompt", settings, isFreshInteractiveClaim: false, account, shell)));

    private static string PullRequestReview(
        string worktree, string settings, ReviewLapGuardOutcome guard, ClaudeCodeAccount account, LaunchShell shell) =>
        Normalized(ScopedAnsiConsoleCapture.Capture(() => PullRequestReviewCommand.PrintHandoff(
            TaskId, worktree, settings, guard, "the briefing", account, shell)));

    private static string Normalized(string text) => text.Replace("\r\n", "\n");

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";
}
