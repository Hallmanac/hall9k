using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// The 2026-09-02 finding, verified end to end: a compile-time constant mirroring
    /// <c>DaemonOptions.VerifyGateTimeout</c>'s own default went stale the moment an operator
    /// raised the live option, since nothing spawned actually read it. <c>ClaudeExecutor</c> now
    /// resolves <c>IOptions&lt;DaemonOptions&gt;</c> exactly as <c>VerificationRunner</c> already
    /// does, so this asserts the settings file a spawned session receives is sized to the
    /// CONFIGURED ceiling, not the type's default.
    /// </summary>
    [Fact]
    public async Task A_raised_verify_gate_timeout_lands_in_the_spawned_sessions_settings_file()
    {
        string runDirectory = Directory.CreateTempSubdirectory("hall9k-claude-executor-tests-").FullName;
        try
        {
            TimeSpan configuredTimeout = TimeSpan.FromMinutes(30);
            ClaudeExecutor executor = new(
                NullLogger<ClaudeExecutor>.Instance, new FakeProcessManager(),
                Options.Create(new DaemonOptions { VerifyGateTimeout = configuredTimeout }));

            AgentSpawnRequest request = new(
                DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", runDirectory, "prompt",
                ExecutorMode.Subscription, AgentModel.Sonnet, SkipPermissions: false);

            await executor.SpawnAsync(request, CancellationToken.None);

            string settingsContent = await File.ReadAllTextAsync(RunPaths.SettingsFile(runDirectory));
            using JsonDocument document = JsonDocument.Parse(settingsContent);
            string defaultTimeoutMilliseconds =
                document.RootElement.GetProperty("env").GetProperty("BASH_DEFAULT_TIMEOUT_MS").GetString()!;

            defaultTimeoutMilliseconds.Should().Be(
                ((long)configuredTimeout.TotalMilliseconds).ToString(),
                "the configured VerifyGateTimeout, not ClaudeSettingsFile.DefaultCommandTimeout, " +
                "must reach the session a foreground gate run actually runs inside");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }
}
