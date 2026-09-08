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
/// <para>
/// Two of the tests here are about what a server with no Hall9k schema says, and one of them
/// leaves a schema behind — which used to make them irreconcilable in one class, and put
/// <c>Assume_yes_creates_the_schema_without_asking</c> in a class (and so a container) of its own
/// so that neither depended on xUnit's undocumented ordering within a class. Both now ask their
/// question of a database of their own on this fixture's container
/// (<see cref="FreshDatabaseAsync"/>) rather than of the container's own history, which is both
/// stricter than what they had — neither is at the mercy of what touched the container first — and
/// what lets one container serve them.
/// </para>
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
        string untouched = await FreshDatabaseAsync(CancellationToken.None);

        ReachabilityReport reachability = await DatabaseReachability.ProbeAsync(untouched, CancellationToken.None);

        reachability.Status.Should().Be(ReachabilityStatus.Reachable);
        (await DatabaseReachability.SchemaPresentAsync(untouched, CancellationToken.None)).Should().BeFalse(
            "nothing has opened a Marten session against this database yet");
    }

    [Fact]
    public async Task Running_the_full_check_against_a_healthy_server_reports_success()
    {
        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            RecordingProcessRunner runner = RecordingProcessRunner.Failing("docker not reached in this test");

            string? healthyConnectionString =
                await DatabaseDoctor.RunAsync(offerFixes: false, assumeYes: false, runner.Runner, CancellationToken.None);

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

    /// <summary>
    /// The schema half of <c>h9k doctor --yes</c> (Windows install friction log item 3): once a
    /// server is reachable but Hall9k's schema is not there yet, <c>assumeYes</c> has to create it
    /// without anybody around to answer "Shall I set that up now?" — the same case that used to
    /// fall through to "It will be created automatically the next time a command touches the
    /// database" and leave a scripted <c>h9k doctor --yes</c> reporting success with no schema
    /// actually applied.
    /// </summary>
    [Fact]
    public async Task Assume_yes_creates_the_schema_without_asking()
    {
        string untouched = await FreshDatabaseAsync(CancellationToken.None);

        string? previous = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, untouched);
        try
        {
            (await DatabaseReachability.SchemaPresentAsync(untouched, CancellationToken.None))
                .Should().BeFalse("a database of this test's own, which nothing has touched");

            RecordingProcessRunner runner = RecordingProcessRunner.Failing("docker not reached — the server is already reachable");

            string? resolved = await DatabaseDoctor.RunAsync(offerFixes: true, assumeYes: true, runner.Runner, CancellationToken.None);

            resolved.Should().Be(untouched);
            runner.Calls.Should().BeEmpty("a reachable server with --yes never needs Docker at all, only the schema apply");
            (await DatabaseReachability.SchemaPresentAsync(untouched, CancellationToken.None))
                .Should().BeTrue("--yes has to apply the schema itself, with nobody there to answer \"Shall I set that up now?\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previous);
        }
    }

    /// <summary>
    /// A database of the caller's own on this fixture's container, so a question about what a
    /// server with no Hall9k schema says can be asked without depending on nothing else having
    /// touched the container first. Creating a database is milliseconds where starting a container
    /// is seconds, and a fresh database is the isolation these two tests actually need.
    /// </summary>
    private async Task<string> FreshDatabaseAsync(CancellationToken cancellationToken)
    {
        string name = $"h9k_doctor_{Guid.NewGuid():N}";

        await using (NpgsqlConnection admin = new(postgres.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);

            // CREATE DATABASE takes no parameters, so the identifier has to be interpolated. It is
            // a fresh UUID rendered as hex — nothing here can carry a quote — and it is quoted
            // anyway, because an unquoted identifier built by concatenation is a habit worth not
            // having even where this one is provably safe.
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }
}
