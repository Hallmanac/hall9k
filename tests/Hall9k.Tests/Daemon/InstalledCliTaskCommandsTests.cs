using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The child <c>h9k</c> the daemon runs for a plan, against a stand-in binary at the installed location: the arguments, the
/// store the child is pointed at, the variables that mark a dispatched session cleared, stdin closed, the output kept in a
/// file, and a child that outruns its bound killed. The stand-in is a shell script, so these run where one can.
/// </summary>
[Collection("Environment")]
[Trait("Category", "RealProcessSpawn")]
public sealed class InstalledCliTaskCommandsTests : IDisposable
{
    private readonly ScopedTestHome _home = new();

    public void Dispose() => _home.Dispose();

    private static InstalledCliTaskCommands Commands(TimeSpan? bound = null) =>
        new(
            new DaemonConnection("Host=plan-test;Database=x"),
            Options.Create(new DaemonOptions { PlanCommandTimeout = bound ?? TimeSpan.FromSeconds(30) }),
            NullLogger<InstalledCliTaskCommands>.Instance);

    private static void InstallStandIn(string script)
    {
        Directory.CreateDirectory(DaemonRuntime.BinDirectory);
        string path = Path.Combine(DaemonRuntime.BinDirectory, "h9k");
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task A_revise_runs_the_installed_binary_with_the_file_the_daemons_store_and_no_dispatched_marker_and_keeps_the_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        InstallStandIn(
            "echo \"args: $@\"\necho \"conn: $HALL9K_CONNECTION_STRING\"\necho \"dispatched: ${HALL9K_DISPATCHED_RUN_ID:-unset}/${HALL9K_DISPATCHED_TASK_ID:-unset}\"\n"
            + "if read line; then echo \"stdin: open\"; else echo \"stdin: closed\"; fi\necho oops 1>&2\nexit 0\n");
        Guid plan = Guid.NewGuid();
        Guid session = Guid.NewGuid();
        Guid task = Guid.NewGuid();
        using EnvironmentVariableScope marked = EnvironmentVariableScope.Set(
            (DispatchedRunEnvironment.RunIdVariable, "a-run"), (DispatchedRunEnvironment.TaskIdVariable, "a-task"));

        TaskCommandResult result = await Commands().ReviseAsync(plan, session, task, "/runs/s/refinement-revision.md", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.OutputFile.Should().Be(RunPaths.PlanReviseOutputFile(plan, session));
        string output = await File.ReadAllTextAsync(result.OutputFile);
        output.Should().Contain($"args: task revise {task} --file /runs/s/refinement-revision.md")
            .And.Contain("conn: Host=plan-test;Database=x")
            .And.Contain("dispatched: unset/unset")
            .And.Contain("stdin: closed")
            .And.Contain("oops");
    }

    [Theory]
    [InlineData(false, false, "task publish {0} --no-queue")]
    [InlineData(true, false, "task publish {0} --no-queue --no-existing-item")]
    [InlineData(false, true, "task publish {0} --no-queue --untracked")]
    public async Task A_publish_passes_no_queue_and_the_attestation_the_plan_recorded(bool noExistingItem, bool untracked, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        InstallStandIn("echo \"args: $@\"\nexit 3\n");
        Guid plan = Guid.NewGuid();
        Guid task = Guid.NewGuid();

        TaskCommandResult result = await Commands().PublishAsync(plan, task, noExistingItem, untracked, CancellationToken.None);

        result.ExitCode.Should().Be(3);
        result.Succeeded.Should().BeFalse("a non-zero exit is a refusal until the task itself says otherwise");
        (await File.ReadAllTextAsync(RunPaths.PlanPublishOutputFile(plan, task))).Should().Contain("args: " + string.Format(expected, task));
    }

    [Fact]
    public async Task A_child_that_outruns_its_bound_is_killed_and_reported_as_timed_out()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        InstallStandIn("echo started\nsleep 30\n");

        TaskCommandResult result = await Commands(TimeSpan.FromMilliseconds(400)).PublishAsync(
            Guid.NewGuid(), Guid.NewGuid(), noExistingItem: false, untracked: false, CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
    }
}
