using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Task: a verify gate can be scoped to a path set (TaylorDenny, arx-platform #2218).
/// <see cref="GatePathScope"/> and <see cref="GatePathScopeOutcome"/> are pure functions, so every
/// case here is a plain in-memory call: no branch, no commit, no container, no git.
/// </summary>
public sealed class GatePathScopeTests
{
    private static readonly IReadOnlyList<string> CompiledDefaults = NonExecutablePathDefaults.Rules;

    private static readonly VerifyCommand Build = new(
        "build", "dotnet build", PathSet: ["**", "!Web/react-apps/customer-portal/", "!Playwright/"]);

    private static readonly VerifyCommand DotnetTest = new(
        "test", "dotnet test", PathSet: ["**", "!Web/react-apps/customer-portal/", "!Playwright/"]);

    private static readonly VerifyCommand Vitest = new(
        "vitest", "npm test", PathSet: ["Web/react-apps/customer-portal/"]);

    private static readonly VerifyCommand[] ThreeGates = [Build, DotnetTest, Vitest];

    private static string[] RunningGates(IReadOnlyList<string>? changedPaths, params VerifyCommand[] gates) =>
        [.. GatePathScope.Decide(changedPaths, CompiledDefaults, gates).Where(d => d.Runs).Select(d => d.Gate.Name)];

    [Fact]
    public void A_front_end_only_diff_skips_the_dotnet_gates_and_runs_the_vitest_gate()
    {
        string[] changed =
        [
            "Web/react-apps/customer-portal/src/components/OrderSummary.ts",
            "Web/react-apps/customer-portal/src/hooks/useOrders.ts",
            "Web/react-apps/customer-portal/package.json",
            "Playwright/tests/orders.spec.ts",
        ];

        RunningGates(changed, ThreeGates).Should().Equal("vitest");
    }

    [Fact]
    public void A_diff_that_also_touches_a_backend_path_runs_all_three_gates()
    {
        string[] changed =
        [
            "Web/react-apps/customer-portal/src/components/OrderSummary.ts",
            "Playwright/tests/orders.spec.ts",
            "Services/Orders/OrderService.cs",
        ];

        RunningGates(changed, ThreeGates).Should().Equal("build", "test", "vitest");
    }

    [Fact]
    public void A_readme_plus_front_end_diff_still_skips_the_dotnet_gates()
    {
        string[] changed =
        [
            "README.md",
            "docs/customer-portal.md",
            "Web/react-apps/customer-portal/src/index.ts",
        ];

        RunningGates(changed, ThreeGates).Should().Equal("vitest");
    }

    [Fact]
    public void A_documentation_only_diff_wakes_no_scoped_gate()
    {
        RunningGates(["README.md", "docs/cli.md"], ThreeGates).Should().BeEmpty(
            "paths in the non-executable set never wake a scoped gate");
    }

    [Fact]
    public void A_null_path_list_runs_every_gate()
    {
        RunningGates(null, ThreeGates).Should().Equal("build", "test", "vitest");
    }

    [Fact]
    public void An_empty_path_list_runs_every_gate()
    {
        RunningGates([], ThreeGates).Should().Equal("build", "test", "vitest");
    }

    [Fact]
    public void An_unscoped_gate_always_runs()
    {
        VerifyCommand unscoped = new("lint", "dotnet format --verify-no-changes");

        RunningGates(["Web/react-apps/customer-portal/src/index.ts"], unscoped, Vitest)
            .Should().Equal("lint", "vitest");
        RunningGates(["README.md"], unscoped, Vitest).Should().Equal("lint");
    }

    [Fact]
    public void A_gate_with_an_empty_path_set_reads_as_unscoped()
    {
        VerifyCommand empty = new("test", "dotnet test", PathSet: []);

        RunningGates(["README.md"], empty).Should().Equal("test");
    }

    [Fact]
    public void An_exclusion_wins_over_a_positive_glob_in_the_same_set()
    {
        VerifyCommand gate = new("test", "dotnet test", PathSet: ["src/", "!src/generated/"]);

        RunningGates(["src/generated/Client.cs"], gate).Should().BeEmpty();
        RunningGates(["src/generated/Client.cs", "src/Orders.cs"], gate).Should().Equal("test");
    }

    [Fact]
    public void The_decisions_keep_the_recorded_gate_order()
    {
        IReadOnlyList<GatePathScope.GateDecision> decisions = GatePathScope.Decide(
            ["Web/react-apps/customer-portal/src/index.ts"], CompiledDefaults, ThreeGates);

        decisions.Select(decision => decision.Gate.Name).Should().Equal("build", "test", "vitest");
        decisions.Select(decision => decision.Runs).Should().Equal(false, false, true);
    }

    [Theory]
    [InlineData("a/**/b", "a/b", false)]
    [InlineData("a/**/b", "a/x/b", true)]
    [InlineData("a/", "a/b/c.cs", true)]
    public void A_positive_glob_that_misses_a_path_skips_its_gate(string glob, string path, bool runs)
    {
        VerifyCommand gate = new("test", "dotnet test", PathSet: [glob]);

        RunningGates([path], gate).Should().Equal(runs ? new[] { "test" } : []);
    }

    [Theory]
    [InlineData(new[] { "**" }, true)]
    [InlineData(new[] { "!docs/" }, false)]
    [InlineData(new[] { "!a/", "!b/" }, false)]
    [InlineData(new[] { "!a/", "b/" }, true)]
    public void A_path_set_needs_a_positive_glob(string[] pathSet, bool expected)
    {
        GatePathScope.HasPositiveGlob(pathSet).Should().Be(expected);
    }

    [Fact]
    public void A_skipped_gate_is_recorded_with_zero_duration_passed_and_the_path_scope_flag()
    {
        GateDuration recorded = GatePathScopeOutcome.SkippedDuration(Build);

        recorded.Should().Be(new GateDuration(
            "build", TimeSpan.Zero, Passed: true, RanFullScope: true, HostCoupledSkipped: false, PathScopeSkipped: true));
    }

    [Fact]
    public void A_pass_where_every_gate_was_skipped_records_a_note_of_its_own()
    {
        IReadOnlyList<GatePathScope.GateDecision> decisions = GatePathScope.Decide(
            ["Web/react-apps/customer-portal/src/index.ts"], CompiledDefaults, [Build, DotnetTest]);

        string? note = GatePathScopeOutcome.AllSkippedNote(decisions);

        note.Should().Contain("path set").And.Contain("skipped");
        note.Should().NotContain("No verification gates configured");
    }

    [Fact]
    public void No_note_is_recorded_when_a_gate_ran_or_none_is_configured()
    {
        GatePathScopeOutcome.AllSkippedNote(
            GatePathScope.Decide(["Services/A.cs"], CompiledDefaults, ThreeGates)).Should().BeNull();
        GatePathScopeOutcome.AllSkippedNote(GatePathScope.Decide(["Services/A.cs"], CompiledDefaults, [])).Should().BeNull();
    }
}
