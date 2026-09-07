using FluentAssertions;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The model an orchestrator recipe's settings.json is rendered for (task: an operator starts a
/// lean node or project orchestrator window) is its own chain, independent of the agent-dispatch
/// one: an orchestrator-specific override outranks everything else, so raising or lowering the
/// model dispatched agents run on never silently moves the operator's own window (independent
/// pre-PR review, cycle 1, conformance lens).
/// </summary>
public sealed class OrchestratorModelTests
{
    [Fact]
    public void ForNode_falls_through_to_the_platform_fallback_when_nothing_is_configured()
    {
        OrchestratorModel.ForNode(new OperatingSettings()).Should().Be(AgentModel.PlatformFallback);
    }

    [Fact]
    public void ForNode_falls_through_to_the_agent_dispatch_default_when_no_orchestrator_override_is_set()
    {
        OperatingSettings settings = new() { DefaultModel = "claude-sonnet-5" };

        OrchestratorModel.ForNode(settings).Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void ForNode_prefers_its_own_override_over_the_agent_dispatch_default()
    {
        OperatingSettings settings = new() { DefaultModel = "claude-sonnet-5", OrchestratorModel = "claude-opus-5" };

        OrchestratorModel.ForNode(settings).Should().Be("claude-opus-5");
    }

    [Fact]
    public void ForNode_treats_a_hand_edited_default_literal_as_unset_at_every_level()
    {
        // A hand-edited config.json can carry the clearing word "default" (h9k config set
        // --default-model's own documented meaning) instead of running the command, and
        // AgentModel.Resolve maps that same literal to Unknown for every dispatched agent — this
        // chain must agree, or the orchestrator window and dispatched agents end up on different
        // models from the same file (independent pre-PR review, cycle 1, adversarial lens).
        OperatingSettings settings = new() { DefaultModel = "claude-sonnet-5", OrchestratorModel = "default" };

        OrchestratorModel.ForNode(settings).Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void ForNode_treats_a_blank_default_model_as_unset()
    {
        OperatingSettings settings = new() { DefaultModel = "   ", OrchestratorModel = null };

        OrchestratorModel.ForNode(settings).Should().Be(AgentModel.PlatformFallback);
    }

    [Fact]
    public void ForProject_falls_through_to_the_node_when_the_project_sets_neither_override()
    {
        OperatingSettings settings = new() { DefaultModel = "claude-sonnet-5" };

        OrchestratorModel.ForProject(AgentModel.Unknown, AgentModel.Unknown, settings).Should().Be("claude-sonnet-5");
    }

    [Fact]
    public void ForProject_prefers_the_project_agent_dispatch_model_over_the_node()
    {
        OperatingSettings settings = new() { DefaultModel = "claude-sonnet-5" };

        OrchestratorModel.ForProject(AgentModel.Unknown, AgentModel.Opus, settings).Should().Be(AgentModel.Opus.Value);
    }

    [Fact]
    public void ForProject_prefers_its_own_orchestrator_override_over_everything_else()
    {
        OperatingSettings settings = new() { DefaultModel = "claude-sonnet-5", OrchestratorModel = "claude-opus-5" };

        OrchestratorModel.ForProject(AgentModel.Haiku, AgentModel.Fable, settings).Should().Be(AgentModel.Haiku.Value);
    }
}
