using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The feed courier's own delivery adapter lookup (idea 89471598, piece 3): Claude Code today,
/// case-insensitively, and no fitting adapter for anything else — the acceptance criterion "with
/// no fitting adapter the feed stays undrained and the log says so" starts here.
/// </summary>
public sealed class CourierDeliveryAdapterTests
{
    [Theory]
    [InlineData("claude-code")]
    [InlineData("Claude-Code")]
    [InlineData("CLAUDE-CODE")]
    public void Claude_code_resolves_regardless_of_case(string cli)
    {
        CourierDeliveryAdapterRegistry.ForCli(cli).Should().NotBeNull().And.BeOfType<ClaudeCodeCourierDeliveryAdapter>();
    }

    [Fact]
    public void An_unrecognized_cli_has_no_fitting_adapter()
    {
        CourierDeliveryAdapterRegistry.ForCli("codex").Should().BeNull();
    }

    [Fact]
    public void The_claude_code_adapter_addresses_the_session_by_name_through_send_message()
    {
        ICourierDeliveryAdapter adapter = new ClaudeCodeCourierDeliveryAdapter();

        string instruction = adapter.BuildDeliveryInstruction("hall9k-orchestrator");

        instruction.Should().Contain("SendMessage").And.Contain("hall9k-orchestrator");
    }
}
