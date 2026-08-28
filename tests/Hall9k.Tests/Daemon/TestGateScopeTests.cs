using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="TestGateScope.Scoped"/>'s own honest-fallback boundary: a filter expression the
/// resolver can compute but the platform cannot execute is the one input that must still degrade
/// to <see cref="TestGateScope.Full"/>, the same as every other unmappable condition
/// <see cref="TestScopeResolver"/> already falls back on (conformance review finding — the
/// composed `--filter` had no length guard, so this was the one case that instead failed the gate
/// process at start).
/// </summary>
public sealed class TestGateScopeTests
{
    [Fact]
    public void A_filter_expression_within_the_safety_cap_stays_scoped()
    {
        IReadOnlyList<string> testClasses = [.. Enumerable.Range(0, 10).Select(i => $"WidgetTests{i}")];

        TestGateScope scope = TestGateScope.Scoped(["src/Hall9k.Domain/Widget.cs"], testClasses, "cycle 2 fix");

        scope.IsScoped.Should().BeTrue();
        scope.FilterExpression.Should().NotBeNull();
    }

    [Fact]
    public void A_filter_expression_over_the_safety_cap_falls_back_to_full()
    {
        // Each name is padded well past what a real test class name would be, so the joined
        // filter clears the cap comfortably without needing an implausible class count.
        IReadOnlyList<string> testClasses =
            [.. Enumerable.Range(0, 100).Select(i => $"ReallyQuiteLongTestClassNameThatPadsOutTheFilterExpression{i}")];

        TestGateScope scope = TestGateScope.Scoped(["src/Hall9k.Domain/Widget.cs"], testClasses, "cycle 2 fix");

        scope.IsScoped.Should().BeFalse("a filter this long risks exceeding the platform's own command-line limit");
        scope.FilterExpression.Should().BeNull();
        scope.Reason.Should().Contain("safety cap").And.Contain("cycle 2 fix");
    }
}
