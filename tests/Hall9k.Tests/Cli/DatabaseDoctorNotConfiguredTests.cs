using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
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

        string? resolved = await DatabaseDoctor.RunAsync(offerFixes: false, runner.Runner, CancellationToken.None);

        resolved.Should().BeNull("nothing is configured and no fix was offered");
        runner.Calls.Should().Contain(
            call => call.Arguments.Count > 0 && call.Arguments[0] == "ps",
            "the stopped-container check must run on every invocation, not only ones that go on to offer a fix");
    }

    [Fact]
    public async Task A_stopped_container_is_probed_even_when_the_session_is_not_interactive()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding("exited\n");

        string? resolved = await DatabaseDoctor.RunAsync(offerFixes: true, runner.Runner, CancellationToken.None);

        resolved.Should().BeNull("nothing is configured and this test process is never an interactive console");
        runner.Calls.Should().Contain(
            call => call.Arguments.Count > 0 && call.Arguments[0] == "ps",
            "a non-interactive h9k doctor still has to name a stopped container, not just an interactive one");
    }
}
