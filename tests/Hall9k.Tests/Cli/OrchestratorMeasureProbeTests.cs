using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The fixed method's token-counting half, isolated from the process it shells out to (task: an
/// operator starts a lean node or project orchestrator window; the method has to be the same on
/// every run so numbers compare across projects and across time).
/// </summary>
public sealed class OrchestratorMeasureProbeTests
{
    [Fact]
    public void It_sums_every_input_side_usage_field()
    {
        const string json = """
            {"type":"result","usage":{"input_tokens":120,"cache_creation_input_tokens":900,"cache_read_input_tokens":18000,"output_tokens":4}}
            """;

        OrchestratorMeasureProbe.ParseTurnOneTokens(json).Should().Be(120 + 900 + 18000);
    }

    [Fact]
    public void A_missing_usage_object_is_reported_rather_than_guessed()
    {
        const string json = """{"type":"result"}""";

        Action act = () => OrchestratorMeasureProbe.ParseTurnOneTokens(json);

        act.Should().Throw<DomainValidationException>().WithMessage("*usage*");
    }

    [Fact]
    public void Malformed_output_is_reported_rather_than_guessed()
    {
        Action act = () => OrchestratorMeasureProbe.ParseTurnOneTokens("not json");

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public async Task A_missing_working_directory_is_reported_as_missing_not_as_claude_off_path()
    {
        // OrchestratorRecipeContext.ProjectWorkingDirectory returns a placeholder string
        // ("<no home recorded yet - run h9k project init …>") when nothing is recorded, and that
        // placeholder used to be handed straight to ProcessStartInfo.WorkingDirectory — Process.Start
        // then threw a Win32Exception identical to `claude` missing from PATH, sending the operator
        // after the wrong problem (independent pre-PR review, cycle 3, adversarial lens).
        string missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");

        Func<Task> act = () => OrchestratorMeasureProbe.RunAsync(missing, "recipes/launch-anchor.md", "recipes/settings.json", CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*does not exist*");
    }

    // --- OrchestratorMeasureCommand.AnchorPathIn / SettingsPathIn --------------------------

    [Fact]
    public void A_custom_launch_texts_own_settings_and_anchor_paths_are_read_not_assumed()
    {
        // Before this, h9k orchestrator measure always probed the platform default paths
        // regardless of what the launch text it stamped the result onto actually named
        // (independent pre-PR review, cycle 3, both lenses).
        const string text = "cd \"/home\" && claude --strict-mcp-config --setting-sources project "
            + "--settings other/settings.json --append-system-prompt-file other/anchor.md \"hi\"";

        OrchestratorMeasureCommand.SettingsPathIn(text).Should().Be("other/settings.json");
        OrchestratorMeasureCommand.AnchorPathIn(text).Should().Be("other/anchor.md");
    }

    [Fact]
    public void A_launch_text_with_no_explicit_flags_falls_back_to_the_platform_default_paths()
    {
        const string text = "cd \"/home\" && claude \"hi\"";

        OrchestratorMeasureCommand.SettingsPathIn(text).Should().Be(LaunchTextDefaults.SettingsRelativePath);
        OrchestratorMeasureCommand.AnchorPathIn(text).Should().Be(LaunchTextDefaults.AnchorRelativePath);
    }
}
