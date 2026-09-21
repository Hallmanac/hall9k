using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="LiveGateGuard"/>'s own pure decisions (task: a daemon restart that adopts a run
/// mid-gate ends that gate's orphaned process tree instead of racing it — this is the CLI-side
/// half): <c>h9k daemon stop</c>'s warn-and-proceed, the <c>--restart</c> shared path's bounded
/// wait, and the <c>--now</c> override. Every test here drives <see cref="LiveGateGuard"/>'s own
/// injectable seams with fakes — no real gate, no real daemon, no real process — except the one
/// test that exercises the real store-lookup seam (<see cref="FindOnThisNodeAsync"/>) against a
/// deliberately unconfigured store, which is itself a store the CLI genuinely cannot reach.
/// </summary>
public sealed class LiveGateGuardTests
{
    private static readonly LiveGate SomeGate = new(Guid.NewGuid(), Guid.NewGuid(), "test", 4242);

    [Fact]
    public async Task An_unreachable_store_prints_that_the_check_was_skipped_and_returns_null()
    {
        // A port nothing is listening on, not merely an unconfigured one: this repo's own dev
        // environment (and CI) may well have a real HALL9K_CONNECTION_STRING or a
        // .hall9k-connection file reachable from the real current directory, which
        // ScopedTestHome's bare HALL9K_HOME redirect never touches — pinning the connection
        // string itself is what actually forces the "cannot reach" branch this test exists to
        // cover, rather than accidentally reaching this machine's real store.
        using ScopedTestHome scopedHome = new(
            "Host=127.0.0.1;Port=1;Database=hall9k-unreachable-test;Username=none;Password=none;Timeout=2");

        IReadOnlyList<LiveGate>? result = null;
        string text = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
        {
            result = await LiveGateGuard.FindOnThisNodeAsync(CancellationToken.None);
        });

        result.Should().BeNull("a store this CLI cannot reach is 'unknown', never guessed at as 'nothing running'");
        text.Should().Contain("skipped");
    }

    [Fact]
    public async Task Warning_names_every_live_gate_by_run_task_gate_and_pid()
    {
        LiveGate other = new(Guid.NewGuid(), Guid.NewGuid(), "build", 777);

        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WarnAboutLiveGatesAsync(_ => Task.FromResult<IReadOnlyList<LiveGate>?>([SomeGate, other]), CancellationToken.None));

        text.Should().Contain(SomeGate.RunId.ToString()).And.Contain(SomeGate.TaskId.ToString())
            .And.Contain("'test'").And.Contain("4242");
        text.Should().Contain(other.RunId.ToString()).And.Contain("'build'").And.Contain("777");
    }

    [Fact]
    public async Task Warning_prints_nothing_when_no_gate_is_live()
    {
        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WarnAboutLiveGatesAsync(_ => Task.FromResult<IReadOnlyList<LiveGate>?>([]), CancellationToken.None));

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task Warning_prints_nothing_extra_when_the_check_itself_could_not_run()
    {
        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WarnAboutLiveGatesAsync(_ => Task.FromResult<IReadOnlyList<LiveGate>?>(null), CancellationToken.None));

        // FindOnThisNodeAsync itself already prints the skip message before returning null —
        // this fake bypasses that real seam entirely, so nothing further is printed here.
        text.Should().BeEmpty();
    }

    /// <summary>
    /// The bounded wait re-queries on every poll (task: ... never races it): a gate the first
    /// check finds live can end while a DIFFERENT run's own gate starts inside the same wait,
    /// and the wait must still catch that second one rather than declaring victory the moment
    /// the first one it happened to see is gone.
    /// </summary>
    [Fact]
    public async Task WaitForClearAsync_re_queries_and_catches_a_gate_ending_and_another_starting_inside_it()
    {
        LiveGate second = new(Guid.NewGuid(), Guid.NewGuid(), "test", 5151);
        List<IReadOnlyList<LiveGate>?> responses = [[SomeGate], [second], []];
        int findCalls = 0;
        int delayCalls = 0;

        Task<IReadOnlyList<LiveGate>?> Find(CancellationToken _)
        {
            IReadOnlyList<LiveGate>? response = responses[findCalls];
            findCalls++;
            return Task.FromResult(response);
        }

        Task Delay(TimeSpan _, CancellationToken _2)
        {
            delayCalls++;
            return Task.CompletedTask;
        }

        bool cleared = await LiveGateGuard.WaitForClearAsync(
            Find, Delay, TimeSpan.FromSeconds(30), CancellationToken.None);

        cleared.Should().BeTrue();
        findCalls.Should().Be(3, "the first live gate, the one that replaces it, and the final empty check");
        delayCalls.Should().Be(2, "a delay separates each poll that still found something live");
    }

    [Fact]
    public async Task WaitForClearAsync_returns_true_at_once_when_nothing_is_live_on_the_first_check()
    {
        int delayCalls = 0;
        bool cleared = await LiveGateGuard.WaitForClearAsync(
            _ => Task.FromResult<IReadOnlyList<LiveGate>?>([]),
            (_, _) => { delayCalls++; return Task.CompletedTask; },
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        cleared.Should().BeTrue();
        delayCalls.Should().Be(0, "nothing to wait for means no poll is ever needed");
    }

    [Fact]
    public async Task WaitForClearAsync_gives_up_once_the_deadline_passes_with_something_still_running()
    {
        bool cleared = await LiveGateGuard.WaitForClearAsync(
            _ => Task.FromResult<IReadOnlyList<LiveGate>?>([SomeGate]),
            (_, _) => Task.CompletedTask,
            TimeSpan.Zero,
            CancellationToken.None);

        cleared.Should().BeFalse("the deadline is already spent the moment something is found still running");
    }

    [Fact]
    public async Task WaitUnlessNowAsync_with_now_skips_the_check_entirely()
    {
        bool findCalled = false;
        bool delayCalled = false;

        await LiveGateGuard.WaitUnlessNowAsync(
            now: true,
            _ => { findCalled = true; return Task.FromResult<IReadOnlyList<LiveGate>?>([SomeGate]); },
            (_, _) => { delayCalled = true; return Task.CompletedTask; },
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        findCalled.Should().BeFalse("--now is the explicit override — it restarts at once, without even checking");
        delayCalled.Should().BeFalse();
    }

    [Fact]
    public async Task WaitUnlessNowAsync_without_now_waits_and_reports_when_the_deadline_still_finds_something_running()
    {
        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WaitUnlessNowAsync(
                now: false,
                _ => Task.FromResult<IReadOnlyList<LiveGate>?>([SomeGate]),
                (_, _) => Task.CompletedTask,
                TimeSpan.Zero,
                CancellationToken.None));

        text.Should().Contain("restarting anyway");
    }

    [Fact]
    public async Task WaitUnlessNowAsync_without_now_prints_nothing_once_it_clears()
    {
        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WaitUnlessNowAsync(
                now: false,
                _ => Task.FromResult<IReadOnlyList<LiveGate>?>([]),
                (_, _) => Task.CompletedTask,
                TimeSpan.FromMinutes(30),
                CancellationToken.None));

        text.Should().BeEmpty();
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses: the wait used to print nothing at all
    /// while it polled, so a thirty-minute wait looked identical to a hung CLI. The first live
    /// check must name the gate (run, task, gate name, pid) and mention --now, the same
    /// information <see cref="WarnAboutLiveGatesAsync"/> already prints on the stop path.
    /// </summary>
    [Fact]
    public async Task WaitForClearAsync_announces_the_gate_it_is_waiting_on_and_mentions_now()
    {
        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WaitForClearAsync(
                _ => Task.FromResult<IReadOnlyList<LiveGate>?>([SomeGate]),
                (_, _) => Task.CompletedTask,
                TimeSpan.Zero,
                CancellationToken.None));

        text.Should().Contain(SomeGate.RunId.ToString()).And.Contain(SomeGate.TaskId.ToString())
            .And.Contain("'test'").And.Contain("4242");
        text.Should().Contain("--now");
    }

    /// <summary>
    /// The announcement fires once, on the first live check, not on every poll — a thirty-minute
    /// wait polling every five seconds would otherwise print the same lines well over three
    /// hundred times.
    /// </summary>
    [Fact]
    public async Task WaitForClearAsync_announces_only_once_across_repeated_polls()
    {
        List<IReadOnlyList<LiveGate>?> responses = [[SomeGate], [SomeGate], []];
        int findCalls = 0;

        Task<IReadOnlyList<LiveGate>?> Find(CancellationToken _)
        {
            IReadOnlyList<LiveGate>? response = responses[findCalls];
            findCalls++;
            return Task.FromResult(response);
        }

        string text = await ScopedAnsiConsoleCapture.CaptureAsync(() =>
            LiveGateGuard.WaitForClearAsync(Find, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(30), CancellationToken.None));

        int occurrences = text.Split(SomeGate.RunId.ToString()).Length - 1;
        occurrences.Should().Be(1, "the announcement names the gate once, not on every poll");
    }
}
