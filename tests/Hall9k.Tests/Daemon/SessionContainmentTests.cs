using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The macOS signal fence is a prefix <see cref="ClaudeExecutor"/> puts ahead of the claude command
/// it hands to the process manager. These tests run on every operating system by injecting the
/// platform and the sandbox-exec probe, so the composition and the decision of when to apply it are
/// proven on the Linux and Windows CI legs too; the real seatbelt behaviour is proven by
/// <see cref="SessionContainmentSeatbeltTests"/> on a Mac host.
/// </summary>
public sealed class SessionContainmentTests
{
    [Fact]
    public void The_profile_is_exactly_the_one_rule_the_boundary_was_decided_on()
    {
        SessionContainment.Profile.Should().Be(
            "(version 1)(allow default)(deny signal)(allow signal (target same-sandbox))"
            + "(allow process-exec (literal \"/bin/ps\") (with no-sandbox))");
    }

    [Fact]
    public async Task On_macOS_with_containment_on_the_command_handed_to_the_process_manager_carries_the_wrapper()
    {
        (FakeProcessManager processes, int probes) = await SpawnAsync(isMacOs: true, enabled: true, probe: () => null);

        string command = processes.Spawns.Should().ContainSingle().Which.Command;
        command.Should().StartWith($"{SessionContainment.SandboxExecPath} -p '{SessionContainment.Profile}' ")
            .And.Contain(" -p --output-format stream-json ", "the claude command follows the prefix intact");
        probes.Should().Be(1, "the executor confirms sandbox-exec works before each contained spawn");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Without_macOS_or_with_containment_off_the_command_carries_no_wrapper_and_sandbox_exec_is_never_consulted(
        bool isMacOs, bool enabled)
    {
        (FakeProcessManager processes, int probes) = await SpawnAsync(isMacOs, enabled, probe: () => null);

        string command = processes.Spawns.Should().ContainSingle().Which.Command;
        command.Should().NotContain("sandbox-exec").And.Contain(" -p --output-format stream-json ");
        probes.Should().Be(0);
    }

    [Fact]
    public async Task A_sandbox_exec_that_fails_its_probe_starts_no_agent_and_the_error_names_containment()
    {
        FakeProcessManager processes = new();
        string runDirectory = Directory.CreateTempSubdirectory("hall9k-containment-tests-").FullName;
        try
        {
            ClaudeExecutor executor = Executor(processes, true, true, () => "/usr/bin/sandbox-exec does not exist.");

            Func<Task> act = () => executor.SpawnAsync(Request(runDirectory), CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*Session containment could not be applied*no agent process was started*does not exist*");
            processes.Spawns.Should().BeEmpty("a failed pre-spawn check must leave no agent process behind");
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public void Off_never_wraps_whatever_the_platform()
    {
        SessionContainment.Off.Apply("claude", enabled: true).Should().Be("claude");
    }

    private static async Task<(FakeProcessManager Processes, int Probes)> SpawnAsync(
        bool isMacOs, bool enabled, Func<string?> probe)
    {
        FakeProcessManager processes = new();
        int probes = 0;
        string runDirectory = Directory.CreateTempSubdirectory("hall9k-containment-tests-").FullName;
        try
        {
            ClaudeExecutor executor = Executor(processes, isMacOs, enabled, () =>
            {
                probes++;
                return probe();
            });
            await executor.SpawnAsync(Request(runDirectory), CancellationToken.None);
            return (processes, probes);
        }
        finally
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static ClaudeExecutor Executor(
        FakeProcessManager processes, bool isMacOs, bool enabled, Func<string?> probe) =>
        new(
            NullLogger<ClaudeExecutor>.Instance, processes,
            Options.Create(new DaemonOptions { SessionContainment = enabled }),
            new SessionContainment(isMacOs, probe));

    private static AgentSpawnRequest Request(string runDirectory) =>
        new(
            DomainId.New(), DomainId.New(), "/tmp/ordinary-worktree", runDirectory, "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false)
        {
            SessionName = "test-build",
        };
}
