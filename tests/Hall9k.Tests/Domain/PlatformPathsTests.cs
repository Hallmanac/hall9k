using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="PlatformPaths.Home"/>'s own flow-scoped override (Decisions Log
/// PLACEHOLDER-98484f36) — the seam <c>ScopedTestHome</c> opens so a test's redirected home
/// never races another test's, and never leaks past the scope that opened it.
/// <para>
/// One test here writes the literal <c>HALL9K_HOME</c> environment variable directly, to prove
/// the override outranks it — a genuinely process-wide write with no flow-scoped alternative for
/// that specific proof, so this class carries <c>[Collection("Environment")]</c>.
/// </para>
/// </summary>
[Collection("Environment")]
[Trait("Category", "Environment")]
public sealed class PlatformPathsTests
{
    [Fact]
    public void A_scoped_home_outranks_the_environment_variable()
    {
        string? previousEnvironmentHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
        try
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", "/does/not/matter/env-home");

            using ScopedTestHome scope = new();

            PlatformPaths.Home.Should().Be(
                scope.Home, "the flow-scoped override must outrank the ambient environment variable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", previousEnvironmentHome);
        }
    }

    [Fact]
    public void Disposing_the_scope_restores_whatever_home_resolved_to_before_it_opened()
    {
        string before = PlatformPaths.Home;

        using (new ScopedTestHome())
        {
            PlatformPaths.Home.Should().NotBe(before, "the scope must actually have redirected Home while it is open");
        }

        PlatformPaths.Home.Should().Be(before, "disposing the scope must restore exactly what Home resolved to beforehand");
    }

    [Fact]
    public async Task Two_scopes_on_independent_async_flows_never_observe_each_others_home()
    {
        async Task<(string Opened, string StillOpen)> RunAsync()
        {
            using ScopedTestHome scope = new();
            // Yield so this flow's continuation runs interleaved with the other task below —
            // the point under test is that neither flow's AsyncLocal write is visible to the
            // other, not merely that each is correct read-immediately-after-write.
            await Task.Yield();
            return (scope.Home, PlatformPaths.Home);
        }

        Task<(string Opened, string StillOpen)> first = RunAsync();
        Task<(string Opened, string StillOpen)> second = RunAsync();

        (string firstOpened, string firstStillOpen) = await first;
        (string secondOpened, string secondStillOpen) = await second;

        firstOpened.Should().Be(firstStillOpen, "the first flow must keep observing its own scoped home throughout");
        secondOpened.Should().Be(secondStillOpen, "the second flow must keep observing its own scoped home throughout");
        firstOpened.Should().NotBe(secondOpened, "each scope creates a fresh, independent temporary home");
    }
}
