using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// A pr-review spawn's worktree is another contributor's pull-request head, not something this
/// platform cut itself (adversarial review, cycle 1, `RunLauncher.cs:228`): the flags asserted
/// here are what stop that checkout's own `.claude/settings.json` (hooks included), its
/// project- and local-scoped `CLAUDE.md`/`AGENTS.md`, and its `.mcp.json` from being loaded as
/// live configuration the moment the process starts, before its read-only prompt is ever read.
/// <see cref="AgentSpawnRequest.UntrustedWorkingDirectory"/> is the one signal that turns them
/// on; every other spawn keeps trusting its own worktree exactly as before.
/// </summary>
public sealed class ClaudeExecutorIsolationTests
{
    [Fact]
    public void An_untrusted_working_directory_drops_its_own_settings_and_mcp_config()
    {
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/pr-review-checkout", "/tmp/run", "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, SkipPermissions: false,
            UntrustedWorkingDirectory: true);

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().Contain("--setting-sources user",
            "the checkout's own project and local settings.json — where a hook would live — must never load");
        arguments.Should().Contain("--strict-mcp-config",
            "given with no --mcp-config, this connects to no MCP server rather than whatever the checkout's own .mcp.json names");
    }

    [Fact]
    public void An_ordinary_trusted_worktree_never_gets_the_isolation_flags()
    {
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", "/tmp/run", "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, SkipPermissions: false);

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().NotContain("--setting-sources user",
            "this platform's own worktrees are its own commits; nothing here needs isolating");
        arguments.Should().NotContain("--strict-mcp-config");
    }
}
