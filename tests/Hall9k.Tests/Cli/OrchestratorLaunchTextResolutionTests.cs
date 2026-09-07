using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The read/replace logic every launch-text command shares (task: an operator starts a lean node
/// or project orchestrator window), independent of which scope (node config file, project
/// aggregate) actually stores the list.
/// </summary>
public sealed class OrchestratorLaunchTextResolutionTests
{
    [Fact]
    public void An_unset_claude_code_resolves_to_the_computed_default()
    {
        LaunchText? resolved = OrchestratorLaunchTextResolution.Resolve([], "claude-code", "/home", "hello");

        resolved.Should().NotBeNull();
        resolved!.Text.Should().Contain("/home").And.Contain("hello");
    }

    [Fact]
    public void An_unset_unknown_cli_resolves_to_nothing()
    {
        OrchestratorLaunchTextResolution.Resolve([], "codex", "/home", "hello").Should().BeNull();
    }

    [Fact]
    public void A_stored_entry_wins_over_the_computed_default()
    {
        LaunchText[] stored = [new LaunchText("claude-code", "the stored line")];

        OrchestratorLaunchTextResolution.Resolve(stored, "Claude-Code", "/home", "hello")!.Text
            .Should().Be("the stored line", "the CLI name is matched case-insensitively");
    }

    [Fact]
    public void Setting_new_text_clears_any_prior_measurement()
    {
        LaunchText[] stored = [new LaunchText("claude-code", "old line", MeasuredTurnOneTokens: 21000, MeasuredAt: DateTimeOffset.UtcNow)];

        IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithText(stored, "claude-code", "new line");

        updated.Should().ContainSingle();
        updated[0].Text.Should().Be("new line");
        updated[0].MeasuredTurnOneTokens.Should().BeNull("a measurement observed against the old line says nothing about the new one");
        updated[0].MeasuredAt.Should().BeNull();
    }

    [Fact]
    public void Measuring_stamps_the_result_without_touching_other_entries()
    {
        LaunchText[] stored = [new LaunchText("claude-code", "the line"), new LaunchText("codex", "a different line")];
        LaunchText measured = stored[0];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithMeasurement(stored, measured, 21437, now);

        LaunchText claudeCode = updated.Single(entry => entry.Cli == "claude-code");
        claudeCode.Text.Should().Be("the line");
        claudeCode.MeasuredTurnOneTokens.Should().Be(21437);
        claudeCode.MeasuredAt.Should().Be(now);
        updated.Should().Contain(entry => entry.Cli == "codex" && entry.Text == "a different line");
    }
}
