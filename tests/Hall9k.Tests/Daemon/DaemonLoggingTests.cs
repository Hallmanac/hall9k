using FluentAssertions;
using Hall9k.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class DaemonLoggingTests
{
    [Fact]
    public void Data_access_chatter_is_filtered_out_of_the_daemon_log()
    {
        using ILoggerFactory factory = BuildLoggerFactory();

        // The log is what h9k daemon status tails; an observed 4.7 MB log held 20,955
        // Npgsql.Command lines against 79 from the daemon, which is a tail that shows
        // SQL instead of what the daemon did.
        factory.CreateLogger("Npgsql.Command").IsEnabled(LogLevel.Information).Should().BeFalse();
        factory.CreateLogger("Marten.Events").IsEnabled(LogLevel.Information).Should().BeFalse();
    }

    [Fact]
    public void Data_access_failures_still_reach_the_log()
    {
        using ILoggerFactory factory = BuildLoggerFactory();

        // "Is Postgres running?" is the first question a failed start asks, and the
        // answer arrives as an Npgsql error — filtering the chatter must not filter that.
        factory.CreateLogger("Npgsql.Connection").IsEnabled(LogLevel.Warning).Should().BeTrue();
        factory.CreateLogger("Npgsql.Command").IsEnabled(LogLevel.Error).Should().BeTrue();
    }

    [Fact]
    public void The_daemons_own_categories_keep_logging_at_information()
    {
        using ILoggerFactory factory = BuildLoggerFactory();

        // The catch-up report h9k daemon start tails for is an Information line.
        factory.CreateLogger("Hall9k.Daemon.Dispatch.DispatchLoop").IsEnabled(LogLevel.Information).Should().BeTrue();
    }

    private static ILoggerFactory BuildLoggerFactory()
    {
        ServiceCollection services = new();
        services.AddLogging(logging => DaemonLogging.Configure(logging));
        return services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    }
}
