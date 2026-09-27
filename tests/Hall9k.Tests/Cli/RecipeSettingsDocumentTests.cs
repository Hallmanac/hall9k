using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The generated settings file rides on the launch line with <c>--settings</c> (task: an operator
/// starts a lean node or project orchestrator window) and is the only scope a recipe window
/// actually reads <c>crossSessionInbound</c> from, since <c>--setting-sources project</c> drops
/// user scope entirely.
/// </summary>
public sealed class RecipeSettingsDocumentTests
{
    [Fact]
    public void It_carries_every_field_the_launch_line_depends_on()
    {
        string json = RecipeSettingsDocument.Render("claude-opus-5[1m]");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.GetProperty("crossSessionInbound").GetString().Should().Be("accept");
        root.GetProperty("skipDangerousModePermissionPrompt").GetBoolean().Should().BeTrue();
        root.GetProperty("model").GetString().Should().Be("claude-opus-5[1m]");
        root.TryGetProperty("inputNeededNotifEnabled", out _).Should().BeTrue();
        root.TryGetProperty("agentPushNotifEnabled", out _).Should().BeTrue();
        root.TryGetProperty("effortLevel", out _).Should().BeFalse(
            "no orchestrator effort configured at either level renders byte for byte as it does today");
    }

    /// <summary>
    /// The executor's own effort key (task: the orchestrator window's effort becomes a rendered
    /// project and node setting) — effortLevel for Claude Code, per AgentEffort's own doc.
    /// </summary>
    [Fact]
    public void A_well_formed_effort_renders_as_the_executors_effort_key()
    {
        string json = RecipeSettingsDocument.Render("claude-opus-5[1m]", AgentEffort.High);
        using JsonDocument document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("effortLevel").GetString().Should().Be("high");
    }

    [Theory]
    [MemberData(nameof(NoEffortConfigured))]
    public void No_orchestrator_effort_configured_leaves_the_key_out(AgentEffort? effort)
    {
        string json = RecipeSettingsDocument.Render("claude-opus-5[1m]", effort);
        using JsonDocument document = JsonDocument.Parse(json);

        document.RootElement.TryGetProperty("effortLevel", out _).Should().BeFalse();
    }

    public static TheoryData<AgentEffort?> NoEffortConfigured => new() { null, AgentEffort.Unknown };

    [Fact]
    public void Writing_always_overwrites_whatever_was_there()
    {
        string path = Path.Combine(Path.GetTempPath(), $"recipe-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{}");

            RecipeSettingsDocument.Write(path, "claude-sonnet-5");

            File.ReadAllText(path).Should().Be(RecipeSettingsDocument.Render("claude-sonnet-5"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
