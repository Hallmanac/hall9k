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
/// platform cut itself (adversarial review, cycle 1, `RunLauncher.cs:228`): <c>--setting-sources
/// user</c> is what stops that checkout's own `.claude/settings.json` (hooks included) and its
/// project- and local-scoped `CLAUDE.md`/`AGENTS.md` from being loaded as live configuration the
/// moment the process starts, before its read-only prompt is ever read.
/// <see cref="AgentSpawnRequest.UntrustedWorkingDirectory"/> is the one signal that turns that
/// flag on. <c>--strict-mcp-config</c> is a separate, unconditional policy (task: dispatched
/// sessions stop inheriting account MCP connectors): every spawn carries it, trusted or not, so
/// no dispatched session ever connects to an account MCP server it has no reason to hold.
/// </summary>
public sealed class ClaudeExecutorIsolationTests
{
    [Fact]
    public void An_untrusted_working_directory_drops_its_own_settings_and_mcp_config()
    {
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/pr-review-checkout", "/tmp/run", "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false,
            UntrustedWorkingDirectory: true)
        {
            SessionName = "test-review-adversarial-1",
        };

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().Contain("--setting-sources user",
            "the checkout's own project and local settings.json — where a hook would live — must never load");
        arguments.Should().Contain("--strict-mcp-config",
            "given with no --mcp-config, this connects to no MCP server rather than whatever the checkout's own .mcp.json names");
        arguments.Should().Contain("--name \"test-review-adversarial-1\"",
            "the untrusted-worktree isolation flags are not the only policy this spawn carries");
    }

    [Fact]
    public void An_ordinary_trusted_worktree_never_gets_the_setting_sources_flag()
    {
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", "/tmp/run", "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
        {
            SessionName = "test-build",
        };

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().NotContain("--setting-sources user",
            "this platform's own worktrees are its own commits; nothing here needs isolating");
    }

    [Fact]
    public void Every_spawn_gets_strict_mcp_config_regardless_of_trust()
    {
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", "/tmp/run", "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
        {
            SessionName = "test-build",
        };

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().Contain("--strict-mcp-config",
            "a headless build session has no reason to carry the owner's account MCP connectors " +
            "(Gmail, Slack, Drive, Calendar) either, so the flag is not conditioned on trust");
    }

    /// <summary>
    /// Task: a dispatched session cannot drive the project's own lifecycle. Every daemon dispatch
    /// carries <see cref="DispatchedRunEnvironment.RunIdVariable"/> naming its own run — the CLI's
    /// own interceptor reads this back to refuse a lifecycle verb from inside it — so this asserts
    /// it against <see cref="ProcessSpawnRequest.Environment"/>, the field both process managers
    /// copy verbatim onto the child, rather than against <see cref="ClaudeExecutor.Arguments"/>,
    /// which never carries environment at all.
    /// </summary>
    [Fact]
    public async Task Every_spawn_carries_its_own_run_id_as_the_dispatched_run_environment_variable()
    {
        string runDirectory = Directory.CreateTempSubdirectory("hall9k-claude-executor-tests-").FullName;
        try
        {
            FakeProcessManager processManager = new();
            ClaudeExecutor executor = new(
                NullLogger<ClaudeExecutor>.Instance, processManager,
                Options.Create(new DaemonOptions()));

            Guid runId = DomainId.New();
            AgentSpawnRequest request = new(
                runId, DomainId.New(), "/tmp/ordinary-worktree", runDirectory, "prompt",
                ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
            {
                SessionName = "test-build",
            };

            await executor.SpawnAsync(request, CancellationToken.None);

            processManager.Spawns.Should().ContainSingle()
                .Which.Environment.Should().Contain(
                    new KeyValuePair<string, string>(DispatchedRunEnvironment.RunIdVariable, runId.ToString()),
                    "the spawned child, and every Bash-tool descendant it starts, must be able to observe " +
                    "which run it is — the CLI interceptor refuses a lifecycle verb off exactly this value");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Task: a dispatched session cannot drive the project's own lifecycle (independent pre-PR
    /// review, cycle 1, conformance finding) — <see cref="DispatchedSessionInterceptor"/>'s own
    /// refusal names the task, not only the run, so the task-bound spawn sites have to stamp it too.
    /// </summary>
    [Fact]
    public async Task A_task_bound_spawn_carries_its_own_task_id_as_the_dispatched_task_environment_variable()
    {
        string runDirectory = Directory.CreateTempSubdirectory("hall9k-claude-executor-tests-").FullName;
        try
        {
            FakeProcessManager processManager = new();
            ClaudeExecutor executor = new(
                NullLogger<ClaudeExecutor>.Instance, processManager,
                Options.Create(new DaemonOptions()));

            Guid taskId = DomainId.New();
            AgentSpawnRequest request = new(
                DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", runDirectory, "prompt",
                ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
            {
                TaskId = taskId,
                SessionName = "test-build",
            };

            await executor.SpawnAsync(request, CancellationToken.None);

            processManager.Spawns.Should().ContainSingle()
                .Which.Environment.Should().Contain(
                    new KeyValuePair<string, string>(DispatchedRunEnvironment.TaskIdVariable, taskId.ToString()),
                    "the CLI interceptor's own refusal names which task a dispatched session is inside, not only which run");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Card publication, courier delivery, and project-scoped run-skill discovery all spawn with no
    /// owning task (<see cref="AgentSpawnRequest.TaskId"/>'s own doc) — this proves the variable is
    /// genuinely absent for that shape rather than stamped as an empty or default value.
    /// </summary>
    [Fact]
    public async Task A_spawn_with_no_task_never_carries_the_dispatched_task_environment_variable()
    {
        string runDirectory = Directory.CreateTempSubdirectory("hall9k-claude-executor-tests-").FullName;
        try
        {
            FakeProcessManager processManager = new();
            ClaudeExecutor executor = new(
                NullLogger<ClaudeExecutor>.Instance, processManager,
                Options.Create(new DaemonOptions()));

            AgentSpawnRequest request = new(
                DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", runDirectory, "prompt",
                ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
            {
                SessionName = "test-build",
            };

            await executor.SpawnAsync(request, CancellationToken.None);

            processManager.Spawns.Should().ContainSingle()
                .Which.Environment.Should().NotContain(
                    entry => entry.Key == DispatchedRunEnvironment.TaskIdVariable,
                    "a session with no owning task carries no task-id variable rather than an empty one");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
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
                ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
            {
                SessionName = "test-build",
            };

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
