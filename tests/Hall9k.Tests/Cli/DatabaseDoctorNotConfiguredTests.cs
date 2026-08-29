using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Spectre.Console;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Question 4's reporting duty is independent of the fix-offer that can follow it
/// (Decisions Log #73): a stopped <c>hall9k-postgres</c> container has to be named on every
/// invocation that reaches this diagnosis, not only the interactive ones that go on to offer
/// starting it. No Docker or Postgres needed — <see cref="RecordingProcessRunner"/> stands in
/// for docker, and the connection string is left unconfigured so <see cref="DatabaseDoctor"/>
/// takes the "nothing configured" branch this file is about.
/// </summary>
// HALL9K_HOME and HALL9K_CONNECTION_STRING are process-wide state; sharing the collection
// serializes this against every other test that redirects the same environment.
[Collection("Hall9kHome")]
public sealed class DatabaseDoctorNotConfiguredTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), $"h9k-doctor-not-configured-{Path.GetRandomFileName()}");
    private readonly string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string? previousConnectionString =
        Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);

    public DatabaseDoctorNotConfiguredTests()
    {
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        Directory.Delete(home, recursive: true);
    }

    [Fact]
    public async Task A_stopped_container_is_probed_even_when_fixes_are_not_offered()
    {
        // "docker info" only cares about the exit code (Running); "docker ps -a" reads the
        // same fixed stdout, and "exited" is what a stopped hall9k-postgres reports.
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("exited\n");

        string? resolved = await DatabaseDoctor.RunAsync(offerFixes: false, assumeYes: false, runner.Runner, CancellationToken.None);

        resolved.Should().BeNull("nothing is configured and no fix was offered");
        runner.Calls.Should().Contain(
            call => call.Arguments.Count > 0 && call.Arguments[0] == "ps",
            "the stopped-container check must run on every invocation, not only ones that go on to offer a fix");
    }

    [Fact]
    public async Task A_stopped_container_is_probed_even_when_the_session_is_not_interactive()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("exited\n");

        string? resolved = await DatabaseDoctor.RunAsync(offerFixes: true, assumeYes: false, runner.Runner, CancellationToken.None);

        resolved.Should().BeNull("nothing is configured and this test process is never an interactive console");
        runner.Calls.Should().Contain(
            call => call.Arguments.Count > 0 && call.Arguments[0] == "ps",
            "a non-interactive h9k doctor still has to name a stopped container, not just an interactive one");
    }

    [Fact]
    public async Task A_non_interactive_session_without_yes_skips_the_start_offer_and_names_the_flag()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("exited\n");

        string output = await CaptureAsync(() =>
            DatabaseDoctor.RunAsync(offerFixes: true, assumeYes: false, runner.Runner, CancellationToken.None));

        runner.Calls.Should().NotContain(
            call => call.Arguments.Count > 0 && call.Arguments[0] == "start",
            "a session with nobody there to confirm must never start anything on its own say-so");
        output.Should().Contain("h9k doctor --yes",
            "a skipped prompt has to name the exact flag that answers it, not just advise trying again");
    }

    [Fact]
    public async Task Yes_bypasses_the_interactive_gate_and_attempts_the_fix_anyway()
    {
        // "docker info" (Running) and "docker ps -a" (no container yet — Absent) both
        // succeed with empty output; everything past that (ComposeUpAsync's own
        // "docker volume ls" preflight) fails, which is enough to prove the offer was
        // actually attempted without waiting out a real docker compose up or the 30s
        // Postgres readiness poll that would follow a genuine start.
        List<IReadOnlyList<string>> calls = [];
        ProcessRunner runner = (_, arguments, _, _) =>
        {
            calls.Add(arguments);
            bool recognized = arguments.Count > 0 && arguments[0] is "info" or "ps";
            return Task.FromResult(recognized
                ? new ProcessResult(0, string.Empty, string.Empty)
                : new ProcessResult(1, string.Empty, "docker volume ls failed"));
        };

        string? resolved = await DatabaseDoctor.RunAsync(offerFixes: true, assumeYes: true, runner, CancellationToken.None);

        resolved.Should().BeNull("nothing was actually started — the volume preflight failed");
        calls.Should().Contain(
            call => call.Count > 0 && call[0] == "volume",
            "--yes has to reach the actual start attempt without anybody confirming it first");
    }

    /// <summary>The global console, swapped for a writer so a skipped prompt's own
    /// explanation can be asserted on, then put back — same shape as InstallCommandTests'
    /// own Capture, async because <see cref="DatabaseDoctor.RunAsync(bool, bool, Hall9k.Connectors.Processes.ProcessRunner, CancellationToken)"/> is.</summary>
    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        IAnsiConsole original = AnsiConsole.Console;
        StringWriter writer = new();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 4096;
        AnsiConsole.Console = captured;
        try
        {
            await action();
            return writer.ToString();
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }
}
