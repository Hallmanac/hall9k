using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Queries;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A path-scope skip records a zero duration for a gate that never ran (task: a verify gate can be
/// scoped to a path set), so it is read as a skip by the Gates cell and left out of every
/// duration comparison, the way a host-coupled skip already is.
/// </summary>
public sealed class GateDurationPathScopeTests
{
    [Fact]
    public void The_gates_cell_reads_a_path_skipped_gate_as_skipped_by_path_scope()
    {
        string cell = TaskShowCommand.FormatGateDurations(
        [
            new GateDuration("build", TimeSpan.Zero, Passed: true, PathScopeSkipped: true),
            new GateDuration("vitest", TimeSpan.FromSeconds(30), Passed: true),
        ]);

        cell.Should().Contain("build [dim]skipped (path scope)[/]").And.Contain("vitest");
        cell.Should().NotContain("host-coupled");
    }

    [Fact]
    public void A_path_skipped_sample_never_drags_the_comparison_baseline_down()
    {
        List<GateDuration> samples =
        [
            .. Enumerable.Range(0, GateDurationHistory.MinimumSamplesForComparison)
                .Select(_ => new GateDuration("test", TimeSpan.Zero, Passed: true, PathScopeSkipped: true)),
            .. Enumerable.Range(0, GateDurationHistory.MinimumSamplesForComparison)
                .Select(_ => new GateDuration("test", TimeSpan.FromMinutes(10), Passed: true)),
        ];

        GateDurationComparison? comparison = new GateDurationHistory(samples)
            .Compare("test", TimeSpan.FromMinutes(20), ranFullScope: true);

        comparison.Should().NotBeNull();
        comparison?.RecentAverage.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Only_path_skipped_samples_leave_no_comparison_at_all()
    {
        List<GateDuration> samples =
        [
            .. Enumerable.Range(0, GateDurationHistory.MinimumSamplesForComparison)
                .Select(_ => new GateDuration("test", TimeSpan.Zero, Passed: true, PathScopeSkipped: true)),
        ];

        new GateDurationHistory(samples).Compare("test", TimeSpan.FromMinutes(20), ranFullScope: true).Should().BeNull();
    }
}
