using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Where each half of <c>--restart</c> runs relative to the binary swap
/// (<see cref="InstallCommand.FinishAsync"/>). The ordering is the whole fix: the live-gate wait
/// has to happen while this process's loaded assemblies still match what is on disk, and the stop,
/// the schema repair and the start have to happen afterwards, in a child of the release that was
/// just installed (origin incident, Windows node hall9k-4a, 2026-09-23: a post-swap store open
/// threw <c>TypeLoadException</c> out of the old process, the update ended in a stack trace, and
/// the old daemon was left running).
/// <para>
/// Nothing real is driven here. The gate finder and the child runner are both fakes, the staged
/// "release" is one marker file, and the running daemon is a pid file under this class's own
/// scoped home naming this test process itself — which is genuinely alive, so
/// <c>DaemonProcess.Probe</c> confirms it exactly as it would a real h9kd, with nothing to stop
/// and nothing to start.
/// </para>
/// </summary>
public sealed class InstallCommandRestartOrderTests : IDisposable
{
    private const string Marker = "marker-from-the-new-release.txt";

    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"h9k-restart-order-staging-{Path.GetRandomFileName()}");

    public InstallCommandRestartOrderTests()
    {
        Directory.CreateDirectory(_staging);
        File.WriteAllText(Path.Combine(_staging, Marker), "new release\n");
        PretendADaemonIsRunning();
    }

    public void Dispose()
    {
        _scopedHome.Dispose();
        InstallCommand.TryDelete(_staging);
    }

    private static void PretendADaemonIsRunning()
    {
        using Process self = Process.GetCurrentProcess();
        DaemonPidFile.Write(
            DaemonRuntime.PidFile,
            new DaemonProcessDescriptor(self.Id, new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero)));
    }

    private static bool TheSwapHasHappened() => File.Exists(Path.Combine(DaemonRuntime.BinDirectory, Marker));

    [Fact]
    public async Task The_live_gate_wait_runs_before_the_swap_and_the_child_sequence_after_it()
    {
        bool? swapHadHappenedAtTheGateCheck = null;
        List<bool> swapHadHappenedAtEachChild = [];
        List<string> commandLines = [];

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: true,
            noRestart: false,
            linkOntoPath: false,
            liveGateFinder: _ =>
            {
                swapHadHappenedAtTheGateCheck = TheSwapHasHappened();
                return Task.FromResult<IReadOnlyList<LiveGate>?>([]);
            },
            restartChildRunner: (binary, arguments, _) =>
            {
                swapHadHappenedAtEachChild.Add(TheSwapHasHappened());
                commandLines.Add($"{Path.GetFileNameWithoutExtension(binary)} {string.Join(' ', arguments)}");
                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        swapHadHappenedAtTheGateCheck.Should().BeFalse(
            "the gate query opens a Marten store, which after the swap would be the new release's store opened "
            + "inside a process running the old one");
        commandLines.Should().Equal("h9k daemon stop", "h9k doctor --yes --no-configure", "h9k daemon start");
        swapHadHappenedAtEachChild.Should().AllBeEquivalentTo(
            true, "every step from the stop onward runs in the binary the swap put in place");
    }

    [Fact]
    public async Task The_gate_wait_is_skipped_by_now_and_the_hand_off_still_runs()
    {
        bool gateWasChecked = false;
        List<string> commandLines = [];

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: true,
            noRestart: false,
            now: true,
            linkOntoPath: false,
            liveGateFinder: _ =>
            {
                gateWasChecked = true;
                return Task.FromResult<IReadOnlyList<LiveGate>?>([]);
            },
            restartChildRunner: (_, arguments, _) =>
            {
                commandLines.Add($"h9k {string.Join(' ', arguments)}");
                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        gateWasChecked.Should().BeFalse("--now is the override that skips the wait entirely, and it still means that");
        commandLines.Should().Equal("h9k daemon stop", "h9k doctor --yes --no-configure", "h9k daemon start");
    }

    [Fact]
    public async Task No_restart_swaps_the_binaries_and_never_reaches_the_gate_or_the_child()
    {
        bool gateWasChecked = false;
        bool aChildRan = false;

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: false,
            noRestart: true,
            linkOntoPath: false,
            liveGateFinder: _ =>
            {
                gateWasChecked = true;
                return Task.FromResult<IReadOnlyList<LiveGate>?>([]);
            },
            restartChildRunner: (_, _, _) =>
            {
                aChildRan = true;
                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        TheSwapHasHappened().Should().BeTrue("--no-restart still installs; it only leaves the daemon alone");
        gateWasChecked.Should().BeFalse("there is no restart to hold back");
        aChildRan.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_child_fails_the_install_rather_than_the_post_restart_probe_passing_it()
    {
        // Which steps run, and that the plan stops at the first failure, belong to
        // DaemonRestartHandoffTests and are proved there through the same seam; the one thing
        // only this level can prove is what FinishAsync does with the code that comes back
        // (cycle-1 conformance review, which found the rest of this test duplicated that class).
        // It relays it, rather than falling through to the post-restart probe — which would find
        // this very test process alive under the pid file, read Running, and call a restart that
        // never finished a success. BusinessRule rather than Error says so unambiguously: the
        // probe's own failure path returns Error.
        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: true,
            noRestart: false,
            now: true,
            linkOntoPath: false,
            restartChildRunner: (_, arguments, _) => Task.FromResult(
                arguments[0] == "doctor"
                    ? RestartStepResult.Exited(ExitCodes.BusinessRule)
                    : RestartStepResult.Exited(ExitCodes.Ok)),
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(
            ExitCodes.BusinessRule,
            "a restart that failed partway is not the success the still-live pid file would otherwise report");
    }
}
