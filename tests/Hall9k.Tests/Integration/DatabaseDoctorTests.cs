using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Question 2 and 3 of the doctor check against a real Postgres (Decisions Log #58, #73):
/// nothing-listening lives in <c>DatabaseReachabilityTests</c> without Docker at all, but
/// credentials-rejected, database-missing, and the schema check all need a server that can
/// actually say so.
/// </summary>
// The full-check test points HALL9K_CONNECTION_STRING at the fixture, which is process-wide
// state; sharing the collection serializes this against every other test that redirects it.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class DatabaseDoctorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_reachable_server_with_no_schema_yet_says_so()
    {
        ReachabilityReport reachability = await DatabaseReachability.ProbeAsync(postgres.ConnectionString, CancellationToken.None);

        reachability.Status.Should().Be(ReachabilityStatus.Reachable);
        (await DatabaseReachability.SchemaPresentAsync(postgres.ConnectionString, CancellationToken.None)).Should().BeFalse(
            "nothing has opened a Marten session against this fresh container yet");
    }

    [Fact]
    public async Task Running_the_full_check_against_a_healthy_server_reports_success()
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            RecordingProcessRunner runner = RecordingProcessRunner.Failing("docker not reached in this test");

            string? healthyConnectionString = await DatabaseDoctor.RunAsync(offerFixes: false, runner.Runner, CancellationToken.None);

            healthyConnectionString.Should().Be(postgres.ConnectionString);
            runner.Calls.Should().BeEmpty("a reachable server never needs to probe Docker at all");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public async Task Rejected_credentials_are_named_separately_from_nothing_listening()
    {
        NpgsqlConnectionStringBuilder builder = new(postgres.ConnectionString) { Password = "definitely-wrong" };

        ReachabilityReport report = await DatabaseReachability.ProbeAsync(builder.ConnectionString, CancellationToken.None);

        report.Status.Should().Be(ReachabilityStatus.AuthenticationFailed);
    }

    [Fact]
    public async Task A_database_that_does_not_exist_is_named_separately_too()
    {
        NpgsqlConnectionStringBuilder builder = new(postgres.ConnectionString) { Database = "no_such_database" };

        ReachabilityReport report = await DatabaseReachability.ProbeAsync(builder.ConnectionString, CancellationToken.None);

        report.Status.Should().Be(ReachabilityStatus.DatabaseMissing);
    }
}
