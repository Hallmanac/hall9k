using FluentAssertions;
using Hall9k.Cli.Diagnostics;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Question 2 of the doctor check without a database at all: "nothing is listening" is
/// reachable without Docker or Postgres — an unused local port refuses the connection the
/// same way a stopped or never-started Postgres would (Decisions Log #58, #73). The
/// credential-rejected and schema questions need a real server and live in
/// <c>DatabaseDoctorTests</c> instead.
/// </summary>
public sealed class DatabaseReachabilityTests
{
    [Fact]
    public async Task Nothing_listening_is_refused_connection_not_a_generic_error()
    {
        ReachabilityReport report = await DatabaseReachability.ProbeAsync(
            "Host=127.0.0.1;Port=1;Database=hall9k;Username=postgres;Password=hall9k;Timeout=2", CancellationToken.None);

        report.Status.Should().Be(ReachabilityStatus.RefusedConnection);
        report.Host.Should().Be("127.0.0.1");
        report.Port.Should().Be(1);
    }

    [Fact]
    public async Task An_unparseable_connection_string_is_named_rather_than_thrown()
    {
        ReachabilityReport report = await DatabaseReachability.ProbeAsync("not a connection string \x01", CancellationToken.None);

        report.Status.Should().Be(ReachabilityStatus.OtherError);
    }
}
