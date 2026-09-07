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
        text.Should().Contain(WorkingDirectory);
        text.Should().Contain(OpeningMessage);
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
