using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Installation;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
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
/// and nothing to start. A test modelling the already-stopped starting state deletes that pid
/// file first, so the same probe genuinely comes back null instead.
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
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        swapHadHappenedAtTheGateCheck.Should().BeFalse(
            "the gate query opens a Marten store, which after the swap would be the new release's store opened "
            + "inside a process running the old one");
        commandLines.Should().Equal(
            "h9k daemon stop", "h9k doctor --yes --no-configure", "h9k daemon start", "h9k orchestrator refresh-anchors");
        swapHadHappenedAtEachChild.Should().AllBeEquivalentTo(
            true, "every step from the stop onward runs in the binary the swap put in place");
        commandLines.Last().Should().Be(
            "h9k orchestrator refresh-anchors",
            "the refresh reads the project registry, which a schema-changing release only allows once the doctor step has run");
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
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        gateWasChecked.Should().BeFalse("--now is the override that skips the wait entirely, and it still means that");
        commandLines.Should().Equal(
            "h9k daemon stop", "h9k doctor --yes --no-configure", "h9k daemon start", "h9k orchestrator refresh-anchors");
    }

    /// <summary>
    /// The fix for the medium finding from independent pre-PR review (cycle 1, adversarial lens):
    /// with no daemon running to begin with, there is nothing supervising a gate for the wait to
    /// protect — any live pid the query would find is already an orphan from an earlier stop — so
    /// the wait is skipped the same as <c>--now</c>, without needing <c>--now</c> passed at all.
    /// </summary>
    [Fact]
    public async Task The_gate_wait_is_skipped_when_the_daemon_was_already_stopped()
    {
        File.Delete(DaemonRuntime.PidFile);

        bool gateWasChecked = false;
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
                gateWasChecked = true;
                return Task.FromResult<IReadOnlyList<LiveGate>?>([]);
            },
            restartChildRunner: (_, arguments, _) =>
            {
                commandLines.Add($"h9k {string.Join(' ', arguments)}");
                if (arguments is ["daemon", "start"])
                {
                    // Models what the real h9k daemon start step would have left behind — a live
                    // pid — so the post-restart probe RestartThroughNewBinaryAsync runs afterward
                    // reads the daemon as up, exactly as it would for a real successful start.
                    PretendADaemonIsRunning();
                }

                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        gateWasChecked.Should().BeFalse(
            "there is no daemon supervising a gate for the wait to protect when none was running to begin with");
        commandLines.Should().Equal(
            "h9k daemon stop", "h9k doctor --yes --no-configure", "h9k daemon start", "h9k orchestrator refresh-anchors");
    }

    [Fact]
    public async Task No_restart_swaps_the_binaries_and_never_reaches_the_gate_or_a_restart_step()
    {
        bool gateWasChecked = false;
        List<string> commandLines = [];

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
            restartChildRunner: (_, arguments, _) =>
            {
                commandLines.Add($"h9k {string.Join(' ', arguments)}");
                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        TheSwapHasHappened().Should().BeTrue("--no-restart still installs; it only leaves the daemon alone");
        gateWasChecked.Should().BeFalse("there is no restart to hold back");
        commandLines.Should().Equal(
            ["h9k orchestrator refresh-anchors"], "the anchor refresh is the only child --no-restart still launches");
    }

    /// <summary>
    /// The anchor refresh goes through the installed CLI, so it has to be launched after the swap
    /// that put that CLI in place, and it must not depend on <c>--restart</c>: a teammate who runs
    /// only <c>h9k update</c> still needs their project windows' anchors brought current. The
    /// <c>--restart</c> ordering is proved by
    /// <see cref="The_live_gate_wait_runs_before_the_swap_and_the_child_sequence_after_it"/>, so only
    /// the <c>--no-restart</c> boundary earns a case here.
    /// </summary>
    [Fact]
    public async Task The_anchor_refresh_is_launched_after_the_swap_under_no_restart()
    {
        List<bool> swapHadHappenedAtTheRefresh = [];

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: false,
            noRestart: true,
            linkOntoPath: false,
            restartChildRunner: (_, arguments, _) =>
            {
                if (arguments is ["orchestrator", "refresh-anchors"])
                {
                    swapHadHappenedAtTheRefresh.Add(TheSwapHasHappened());
                }

                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        swapHadHappenedAtTheRefresh.Should().Equal(
            [true], "the refresh runs once, in the release the swap put in place and not in the one being replaced");
    }

    /// <summary>
    /// A hand-off that fails partway still gets the refresh: a failed daemon start says nothing
    /// about whether the anchors can be re-rendered, and the node's own anchor needs no registry.
    /// </summary>
    [Fact]
    public async Task A_failed_restart_step_still_runs_the_anchor_refresh_and_keeps_its_own_exit_code()
    {
        List<string> commandLines = [];

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: true,
            noRestart: false,
            now: true,
            linkOntoPath: false,
            restartChildRunner: (_, arguments, _) =>
            {
                commandLines.Add($"h9k {string.Join(' ', arguments)}");
                return Task.FromResult(
                    arguments is ["doctor", ..]
                        ? RestartStepResult.Exited(ExitCodes.Error)
                        : RestartStepResult.Exited(ExitCodes.Ok));
            },
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Error);
        commandLines.Should().Equal(
            "h9k daemon stop", "h9k doctor --yes --no-configure", "h9k orchestrator refresh-anchors");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "no h9k there")]
    public async Task A_refresh_that_did_not_run_says_so_and_leaves_the_exit_code_alone(
        bool exitedNonzero, string? couldNotLaunch)
    {
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            int exitCode = await InstallCommand.FinishAsync(
                _staging,
                skillsSource: null,
                version: "0.0.0-test",
                restart: false,
                noRestart: true,
                linkOntoPath: false,
                restartChildRunner: (_, _, _) => Task.FromResult(
                    exitedNonzero
                        ? RestartStepResult.Exited(ExitCodes.Error)
                        : RestartStepResult.NotLaunched(couldNotLaunch ?? string.Empty)),
                containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
                cancellationToken: CancellationToken.None);

            exitCode.Should().Be(ExitCodes.Ok, "a refresh that cannot run never fails the install");
        });

        output.Should().Contain("No project home's launch anchor was refreshed")
            .And.Contain("h9k orchestrator refresh-anchors")
            .And.Contain("h9k project init <project>");
    }

    /// <summary>
    /// The other half of the same regression: a plain <c>h9k update</c> (no <c>--restart</c>) must
    /// never finish quietly, on either starting state, since the compose rewrite it just made can
    /// leave a real container bound the old way until something actually runs the doctor step
    /// against it. It has to say which command does that, in words an operator can paste.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_restart_names_the_exact_command_that_finishes_the_migration_on_either_starting_state(
        bool daemonWasRunningBefore)
    {
        if (!daemonWasRunningBefore)
        {
            File.Delete(DaemonRuntime.PidFile);
        }

        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            int exitCode = await InstallCommand.FinishAsync(
                _staging,
                skillsSource: null,
                version: "0.0.0-test",
                restart: false,
                noRestart: true,
                linkOntoPath: false,
                commandName: "update",
                containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
                cancellationToken: CancellationToken.None);

            exitCode.Should().Be(ExitCodes.Ok);
        });

        output.Should().Contain("h9k update --restart");
    }

    /// <summary>
    /// A <c>--from-release</c> bootstrap install has no repo checkout for a later
    /// <c>h9k install --restart</c> (default <c>--repo</c>) to publish from — the bootstrap scripts
    /// run <c>h9k install --from-release &lt;payload&gt; --no-restart</c> on a bare machine with
    /// neither a checkout nor the .NET SDK — so the not-restarting message must not point at a
    /// command that would fail there (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task No_restart_leaves_off_the_command_hint_when_it_would_not_work_standing_alone()
    {
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            int exitCode = await InstallCommand.FinishAsync(
                _staging,
                skillsSource: null,
                version: "0.0.0-test",
                restart: false,
                noRestart: true,
                linkOntoPath: false,
                suggestRestartCommand: false,
                containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
                cancellationToken: CancellationToken.None);

            exitCode.Should().Be(ExitCodes.Ok);
        });

        output.Should().NotContain("--restart");
    }

    /// <summary>
    /// The regression this class exists to pin (notes/decisions-2026-09-27-hall9k-6b.md, task
    /// ff8b71b8): with the daemon stopped by hand, <c>h9k update --restart</c> used to short-circuit
    /// on a null <c>DaemonProcess.Probe()</c> before ever asking whether to restart at all — the
    /// compose rewrite landed but the doctor step and the daemon start never ran, leaving the
    /// container bound the old way. <c>--restart</c> now runs the identical hand-off (the doctor
    /// step and the daemon start, the two steps a Postgres migration actually needs) whether or not
    /// a daemon was running to begin with; only the leading "daemon stop" is a no-op in the
    /// already-stopped case, and that no-op is <c>h9k daemon stop</c>'s own job, not this hand-off's.
    /// The running-daemon case is already proved through this same seam by
    /// <see cref="The_gate_wait_is_skipped_by_now_and_the_hand_off_still_runs"/> (independent
    /// pre-PR review, cycle 1, both lenses: the two duplicated each other when this was a
    /// <c>[Theory]</c> covering both states), so only the stopped state earns a case of its own here.
    /// </summary>
    [Fact]
    public async Task Restart_runs_the_doctor_step_and_the_daemon_start_when_the_daemon_was_already_stopped()
    {
        // The constructor pretends one is running by default; this undoes that so
        // DaemonProcess.Probe() genuinely comes back null, the same as a node whose daemon was
        // stopped by hand before the update ran.
        File.Delete(DaemonRuntime.PidFile);

        List<string> commandLines = [];

        int exitCode = await InstallCommand.FinishAsync(
            _staging,
            skillsSource: null,
            version: "0.0.0-test",
            restart: true,
            noRestart: false,
            now: true,
            linkOntoPath: false,
            restartChildRunner: (_, arguments, _) =>
            {
                commandLines.Add($"h9k {string.Join(' ', arguments)}");
                if (arguments is ["daemon", "start"])
                {
                    // Models what the real h9k daemon start step would have left behind — a live
                    // pid — so the post-restart probe RestartThroughNewBinaryAsync runs afterward
                    // reads the daemon as up, exactly as it would for a real successful start.
                    PretendADaemonIsRunning();
                }

                return Task.FromResult(RestartStepResult.Exited(ExitCodes.Ok));
            },
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        commandLines.Should().Contain("h9k doctor --yes --no-configure")
            .And.Contain("h9k daemon start");
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
            containerRuntimeRunner: RecordingProcessRunner.Failing("docker not reached in this test").Runner,
            cancellationToken: CancellationToken.None);

        exitCode.Should().Be(
            ExitCodes.BusinessRule,
            "a restart that failed partway is not the success the still-live pid file would otherwise report");
    }
}
