using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The default claude-code launch line's exact flag set is load-bearing (Decisions Log, the
/// 2026-09-05 held-message incident): a wrong flag is exactly how a recipe window ends up holding
/// a message it should have accepted, or inheriting context it was launched lean specifically to
/// avoid. This test fails outright the moment any of them drops (task: an operator starts a lean
/// node or project orchestrator window).
/// </summary>
public sealed class LaunchTextDefaultsTests
{
    private const string WorkingDirectory = "/some/window/directory";
    private const string OpeningMessage = "You are the test orchestrator.";

    [Fact]
    public void The_default_carries_every_load_bearing_flag()
    {
        string text = LaunchTextDefaults.Render(WorkingDirectory, OpeningMessage);

        text.Should().Contain("--strict-mcp-config");
        text.Should().Contain("--setting-sources project");
        text.Should().Contain("--settings");
        text.Should().Contain(LaunchTextDefaults.SettingsRelativePath);
        text.Should().Contain("--append-system-prompt-file");
        text.Should().Contain(LaunchTextDefaults.AnchorRelativePath);
        text.Should().Contain("--dangerously-skip-permissions");
        text.Should().Contain(WorkingDirectory);
        text.Should().Contain(OpeningMessage);
    }

    [Fact]
    public void A_double_quote_in_the_working_directory_or_opening_message_does_not_break_the_pasted_line()
    {
        // ProjectDecider.Register only rejects a blank project name, and OrchestratorRecipeContext
        // embeds the name straight into the opening message, so a name (or a --home path) carrying
        // a bare '"' used to close the quoted segment early — the pasted line was simply malformed
        // (independent pre-PR review, cycle 3, adversarial lens).
        string text = LaunchTextDefaults.Render("/some/\"quoted\"/directory", "You are the \"web\" project orchestrator.");

        text.Should().NotContain("\"quoted\"/directory\"", "an unescaped quote would close the cd argument early");
        (OperatingSystem.IsWindows() ? text.Contains("`\"") : text.Contains("\\\"")).Should().BeTrue(
            "the embedded quote must be escaped for the host shell rather than passed through raw");
    }

    [Fact]
    public void A_dollar_sign_in_the_working_directory_does_not_trigger_substitution()
    {
        // A project name like web$(id) (ProjectDecider.Register accepts any non-blank name) ends
        // up inside a double-quoted segment, where bash/zsh/PowerShell all still interpolate '$'.
        string text = LaunchTextDefaults.Render("/home/web$(id)", "opening message");

        (OperatingSystem.IsWindows() ? text.Contains("`$") : text.Contains("\\$")).Should().BeTrue(
            "a bare '$' inside a double-quoted segment would be live to the shell that runs the pasted line");
    }

    [Fact]
    public void Only_claude_code_has_a_computed_default()
    {
        LaunchText? claudeCode = LaunchTextDefaults.For("claude-code", WorkingDirectory, OpeningMessage);
        LaunchText? caseInsensitive = LaunchTextDefaults.For("Claude-Code", WorkingDirectory, OpeningMessage);
        LaunchText? somethingElse = LaunchTextDefaults.For("codex", WorkingDirectory, OpeningMessage);

        claudeCode.Should().NotBeNull();
        claudeCode!.Cli.Should().Be(LaunchText.DefaultCli);
        caseInsensitive.Should().NotBeNull("the CLI name is matched case-insensitively");
        somethingElse.Should().BeNull("no other CLI has a computed default yet");
    }
}
