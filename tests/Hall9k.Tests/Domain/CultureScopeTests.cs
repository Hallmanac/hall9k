using System.Globalization;
using FluentAssertions;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="CultureScope"/>'s own contract, stated rather than assumed: the body runs under the
/// named culture, and every way the call can fail fails as <em>this</em> test rather than as the
/// test host.
/// <para>
/// The last of those is a regression test, not a nicety. The helper sets the culture on a thread of
/// its own, and an exception left unhandled on a thread of one's own terminates the process: before
/// the construction and the two assignments moved inside the body's own <c>try</c>, a culture name
/// this host could not resolve took the whole testhost down, and <c>dotnet test</c> reported a
/// crash with no test attributed to it instead of one red case naming the culture. Note which names
/// actually do that: .NET happily manufactures a pseudo-custom culture for a structurally plausible
/// unknown name (<c>fi_FI</c>, <c>zz-ZZ</c>), so only a structurally invalid one throws.
/// </para>
/// </summary>
public sealed class CultureScopeTests
{
    [Theory]
    [InlineData("fi-FI")]
    [InlineData("da-DK")]
    [InlineData("en-US")]
    public void The_body_runs_under_the_named_culture(string culture) =>
        CultureScope.Run(culture, () =>
        {
            CultureInfo.CurrentCulture.Name.Should().Be(culture);
            CultureInfo.CurrentUICulture.Name.Should().Be(culture);
        });

    [Fact]
    public void A_failure_inside_the_body_is_rethrown_to_the_caller()
    {
        Action run = () => CultureScope.Run("fi-FI", () => 1.Should().Be(2));

        run.Should().Throw<Exception>().Which.Message.Should().Contain("Expected");
    }

    [Fact]
    public void An_unresolvable_culture_fails_this_test_rather_than_the_test_host()
    {
        Action run = () => CultureScope.Run("not a culture", () => { });

        run.Should().Throw<CultureNotFoundException>(
                "the helper's own thread would otherwise take the whole testhost down with it, and a crashed " +
                "testhost attributes the failure to no test at all")
            .Which.Message.Should().Contain("not a culture");
    }

    [Fact]
    public void A_task_returning_body_also_runs_under_the_named_culture_and_rethrows_its_failure()
    {
        CultureScope.RunToCompletion("da-DK", async () =>
        {
            await Task.Yield();
            CultureInfo.CurrentCulture.Name.Should().Be("da-DK");
        });

        Action run = () => CultureScope.RunToCompletion(
            "da-DK",
            async () =>
            {
                await Task.Yield();
                1.Should().Be(2);
            });

        run.Should().Throw<Exception>().Which.Message.Should().Contain("Expected");
    }
}
