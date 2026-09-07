using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;
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
    public void Setting_a_blank_cli_throws()
    {
        Action act = () => OrchestratorLaunchTextResolution.WithText([], "  ", "some line");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("A launch-text entry needs a CLI name.");
    }

    [Fact]
    public void Setting_blank_text_throws()
    {
        Action act = () => OrchestratorLaunchTextResolution.WithText([], "claude-code", "  ");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("The launch text for 'claude-code' cannot be blank.");
    }

    [Fact]
    public void ResolveStored_returns_null_when_nothing_was_ever_set_even_for_claude_code()
    {
        // Unlike Resolve, this must never fall back to the computed default: h9k orchestrator
        // measure uses it precisely so measuring an unset CLI cannot materialize the rendered
        // default into storage and freeze it against a later release's own flag changes.
        OrchestratorLaunchTextResolution.ResolveStored([], "claude-code").Should().BeNull();
    }

    [Fact]
    public void ResolveStored_returns_the_stored_entry_case_insensitively()
    {
        LaunchText[] stored = [new LaunchText("claude-code", "the stored line")];

        OrchestratorLaunchTextResolution.ResolveStored(stored, "Claude-Code")!.Text
            .Should().Be("the stored line");
    }

    [Fact]
    public void Measuring_stamps_the_result_without_touching_other_entries()
    {
        LaunchText[] stored = [new LaunchText("claude-code", "the line"), new LaunchText("codex", "a different line")];
        LaunchText measured = stored[0];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithMeasurement(stored, measured, 21437, now, out bool applied);

        applied.Should().BeTrue();
        LaunchText claudeCode = updated.Single(entry => entry.Cli == "claude-code");
        claudeCode.Text.Should().Be("the line");
        claudeCode.MeasuredTurnOneTokens.Should().Be(21437);
        claudeCode.MeasuredAt.Should().Be(now);
        updated.Should().Contain(entry => entry.Cli == "codex" && entry.Text == "a different line");
    }

    [Fact]
    public void Measuring_discards_the_result_when_the_text_changed_underneath_the_probe()
    {
        LaunchText measured = new("claude-code", "the line that was probed");
        LaunchText[] freshlyReloaded = [measured with { Text = "a line set while the probe ran" }];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithMeasurement(freshlyReloaded, measured, 21437, now, out bool applied);

        applied.Should().BeFalse("a concurrent launch-text set replaced the line this measurement was taken against");
        updated.Should().BeEquivalentTo(freshlyReloaded, "the concurrent edit must not be reverted by a stale measurement");
    }

    [Fact]
    public void Measuring_discards_the_result_when_the_entry_was_removed_underneath_the_probe()
    {
        LaunchText measured = new("claude-code", "the line that was probed");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<LaunchText> updated = OrchestratorLaunchTextResolution.WithMeasurement([], measured, 21437, now, out bool applied);

        applied.Should().BeFalse();
        updated.Should().BeEmpty();
    }
}
