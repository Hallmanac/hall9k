using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project;
using Hall9k.Tests.Domain;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Walking a run skill's plan (idea b9b09779, piece 5), against the fake run skill the acceptance
/// criteria name: three steps and one human step. DB-free and process-free — the walker's three
/// side effects are delegates, so this suite proves the ordering, the pause, the resume and the
/// port without starting a server on the machine running it.
/// </summary>
public sealed class LocalLaunchWalkerTests
{
    private const string Worktree = "/tmp/worktree";
    private const string Log = "/tmp/worktree/local-launch.log";
    private const int ChosenPort = 54321;

    private static readonly RunSkillPlan Plan =
        RunSkillSteps.Parse(RunSkillStepsTests.ThreeStepsAndOneHumanStep);

    /// <summary>The human step is first in the plan, so the very first pass stops before running anything.</summary>
    [Fact]
    public async Task The_launch_stops_at_the_human_step()
    {
        Recorder recorder = new();

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 1);

        walk.PausedAtStep.Should().Be(1);
        walk.NextStepNumber.Should().Be(1);
        walk.Completed.Should().BeFalse();
        recorder.Ran.Should().BeEmpty("nothing runs before the step the reviewer has to do");
        recorder.Spawned.Should().BeEmpty();
    }

    [Fact]
    public async Task Continuing_after_the_human_step_runs_the_rest_in_order_and_leaves_the_product_up()
    {
        Recorder recorder = new();

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 2);

        walk.Completed.Should().BeTrue();
        walk.NextStepNumber.Should().Be(5);
        recorder.Ran.Should().Equal("dotnet --version", "dotnet restore");
        recorder.Spawned.Should().ContainSingle()
            .Which.Should().Be($"dotnet run --project src/Api --port {ChosenPort}");
        walk.Processes.Should().ContainSingle().Which.ProcessId.Should().Be(4242);
    }

    /// <summary>
    /// The skill's launch command names a port, so the launch takes an ephemeral one instead of the
    /// project's own — and the address the reviewer is handed carries the port that was actually
    /// used, not the one the document was written with.
    /// </summary>
    [Fact]
    public async Task The_port_is_ephemeral_and_the_address_says_so()
    {
        Recorder recorder = new();

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 2);

        walk.Port.Should().Be(ChosenPort);
        RunSkillLaunchPort.ApplyToAddress(Plan.AddressOrEntryPoint, walk.Port)
            .Should().Be($"http://localhost:{ChosenPort}/swagger");
    }

    /// <summary>
    /// A launch command with nowhere to put a port is run exactly as written, and the launch says
    /// it chose no port rather than reporting one it did not use.
    /// </summary>
    [Fact]
    public async Task A_launch_command_naming_no_port_is_run_as_written()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("## Launch\n\n- `make dev`\n");
        Recorder recorder = new();

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 1, plan);

        walk.Port.Should().BeNull();
        recorder.Spawned.Should().ContainSingle().Which.Should().Be("make dev");
        RunSkillLaunchPort.ApplyToAddress("http://localhost:3000", walk.Port)
            .Should().Be("http://localhost:3000");
    }

    [Fact]
    public async Task A_failing_command_step_stops_the_walk_there_and_quotes_what_it_said()
    {
        Recorder recorder = new() { Fail = "dotnet restore", FailureOutput = "MSB1003: no project found" };

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 2);

        walk.FailedAtStep.Should().Be(3);
        walk.FailureReason.Should().Contain("MSB1003: no project found");
        walk.Completed.Should().BeFalse();
        recorder.Spawned.Should().BeEmpty("the launch step after the failure never runs");
    }

    /// <summary>
    /// A plan whose human step sits after the launch step pauses with the product already up, and
    /// the walk reports both facts — the processes it left running and the step it stopped at — so
    /// the caller records the launch before it records the pause.
    /// </summary>
    [Fact]
    public async Task A_human_step_after_the_launch_pauses_with_the_product_already_running()
    {
        // Built directly rather than parsed: the parse hoists every human step to the front, so no
        // document produces this order. It arises on a resume — the reviewer does the hoisted step,
        // --continue starts at the launch, and a second human step is what waits after it — and the
        // plan the walker is handed there looks exactly like this.
        RunSkillPlan launchThenHuman = new(
            [
                new RunSkillStep(1, RunSkillStepKind.Command, RunSkillDocument.LaunchHeading, "Start it", "make dev"),
                new RunSkillStep(
                    2, RunSkillStepKind.Human, RunSkillDocument.HumanStepsHeading,
                    "Log in as the seeded admin.", string.Empty),
            ],
            "The log prints Listening.",
            "http://localhost:3000");
        Recorder recorder = new();

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 1, launchThenHuman);

        walk.PausedAtStep.Should().Be(2);
        walk.NextStepNumber.Should().Be(2);
        walk.Processes.Should().ContainSingle();
    }

    /// <summary>
    /// Two launch commands share the one port: a skill that starts an API and a worker against it
    /// must not hand them two different numbers.
    /// </summary>
    [Fact]
    public async Task Two_launch_commands_share_one_ephemeral_port()
    {
        RunSkillPlan plan = RunSkillSteps.Parse("""
            ## Launch

            - Start the API: `dotnet run --project src/Api --port 5000`
            - Start the worker against it: `dotnet run --project src/Worker --port 5000`
            """);
        Recorder recorder = new();

        LocalLaunchWalk walk = await recorder.WalkAsync(from: 1, plan);

        walk.Port.Should().Be(ChosenPort);
        recorder.Spawned.Should().Equal(
            $"dotnet run --project src/Api --port {ChosenPort}",
            $"dotnet run --project src/Worker --port {ChosenPort}");
    }

    /// <summary>
    /// A launch is one walk however many passes it takes, so the port survives the pause. A
    /// resumed pass that chose a second one would start the worker on a different number from the
    /// API already listening in front of the reviewer, and the address they are handed would be
    /// the later one.
    /// </summary>
    [Fact]
    public async Task A_resumed_pass_launches_on_the_port_the_earlier_pass_already_chose()
    {
        RunSkillPlan launchThenHumanThenLaunch = new(
            [
                new RunSkillStep(
                    1, RunSkillStepKind.Command, RunSkillDocument.LaunchHeading, "Start the API",
                    "dotnet run --project src/Api --port 5000"),
                new RunSkillStep(
                    2, RunSkillStepKind.Human, RunSkillDocument.HumanStepsHeading,
                    "Log in as the seeded admin.", string.Empty),
                new RunSkillStep(
                    3, RunSkillStepKind.Command, RunSkillDocument.LaunchHeading, "Start the worker",
                    "dotnet run --project src/Worker --port 5000"),
            ],
            "The log prints Listening.",
            "http://localhost:5000");
        Recorder first = new();
        LocalLaunchWalk paused = await first.WalkAsync(from: 1, launchThenHumanThenLaunch);
        paused.Port.Should().Be(ChosenPort);

        Recorder second = new() { FreePort = 60001 };
        LocalLaunchWalk resumed = await second.WalkAsync(from: 3, launchThenHumanThenLaunch, paused.Port);

        resumed.Port.Should().Be(ChosenPort);
        second.Spawned.Should().ContainSingle()
            .Which.Should().Be($"dotnet run --project src/Worker --port {ChosenPort}");
    }

    /// <summary>
    /// Records what the walk asked for instead of doing it. The port source answers with one fixed
    /// number, so a test asserts the port that was chosen rather than whatever this machine had
    /// free.
    /// </summary>
    private sealed class Recorder
    {
        public List<string> Ran { get; } = [];

        public List<string> Spawned { get; } = [];

        /// <summary>The command whose run should report failure, or null for a walk where everything succeeds.</summary>
        public string? Fail { get; init; }

        public string FailureOutput { get; init; } = string.Empty;

        /// <summary>What this pass's port source answers, when a test needs the two passes of one launch to be told different numbers.</summary>
        public int FreePort { get; init; } = ChosenPort;

        public Task<LocalLaunchWalk> WalkAsync(int from, RunSkillPlan? plan = null, int? chosenPort = null) =>
            LocalLaunchWalker.WalkAsync(
                plan ?? Plan, from, chosenPort, Worktree, Log, Run, Spawn, () => FreePort,
                CancellationToken.None);

        private Task<ShellResult> Run(RunSkillStep step, string worktree, CancellationToken cancellationToken)
        {
            worktree.Should().Be(Worktree);
            Ran.Add(step.Command);
            return Task.FromResult(step.Command == Fail
                ? new ShellResult(1, string.Empty, FailureOutput)
                : new ShellResult(0, string.Empty, string.Empty));
        }

        private LaunchedProcess Spawn(RunSkillStep step, string command, string worktree, string log)
        {
            worktree.Should().Be(Worktree);
            log.Should().Be(Log);
            Spawned.Add(command);
            return new LaunchedProcess(4242 + Spawned.Count - 1, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        }
    }
}
