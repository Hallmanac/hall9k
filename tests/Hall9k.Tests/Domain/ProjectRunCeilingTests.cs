using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The per-project run ceiling's rule (Decisions Log #140) — the one both the dispatcher and
/// <c>h9k status</c> read, so it is tested where it lives rather than twice through two surfaces.
/// </summary>
public sealed class ProjectRunCeilingTests
{
    [Fact]
    public void An_uncapped_project_admits_whatever_the_node_leaves_free()
    {
        // The default, and the behaviour every project had before this setting existed: no cap
        // means the node ceiling alone decides, so this rule never says no.
        ProjectRunCeiling uncapped = ProjectRunCeiling.Uncapped(liveRuns: 7);

        uncapped.HasCap.Should().BeFalse();
        uncapped.IsPaused.Should().BeFalse();
        uncapped.AtCap.Should().BeFalse();
        uncapped.Admits(claimedThisSweep: 0).Should().BeTrue();
        uncapped.Admits(claimedThisSweep: 99).Should().BeTrue("nothing here bounds a project with no cap");
    }

    [Fact]
    public void A_cap_admits_up_to_it_counting_this_sweeps_own_claims()
    {
        // The cap fills as the sweep goes: at 2 with one run live, the first candidate is
        // admitted and the second is not — asked per candidate, never once per sweep.
        ProjectRunCeiling ceiling = new(LiveRuns: 1, Cap: 2);

        ceiling.AtCap.Should().BeFalse();
        ceiling.Admits(claimedThisSweep: 0).Should().BeTrue();
        ceiling.Admits(claimedThisSweep: 1).Should().BeFalse("the sweep's own claim filled the cap");
    }

    [Fact]
    public void A_full_cap_admits_nothing_however_idle_the_node_is()
    {
        ProjectRunCeiling full = new(LiveRuns: 1, Cap: 1);

        full.AtCap.Should().BeTrue();
        full.OverCap.Should().BeFalse();
        full.IsPaused.Should().BeFalse("a full cap is raised; a paused one is resumed — different problems");
        full.Admits(claimedThisSweep: 0).Should().BeFalse();
    }

    [Fact]
    public void A_cap_of_zero_is_a_pause_that_holds_work_on_a_completely_idle_project()
    {
        // The whole point of the pause: nothing live, nothing claimed, and no arithmetic that
        // could ever let one through.
        ProjectRunCeiling paused = new(LiveRuns: 0, Cap: 0);

        paused.IsPaused.Should().BeTrue();
        paused.AtCap.Should().BeTrue();
        paused.HasCap.Should().BeTrue("0 is a cap that is set, not a cap that is absent");
        paused.Admits(claimedThisSweep: 0).Should().BeFalse();
    }

    [Fact]
    public void A_project_over_its_cap_says_so_rather_than_owing_the_queue_slots()
    {
        // A cap lowered while runs are live, or a resolved review park re-entering a run whose
        // slot was released: the cap gates claims, it never kills work, so the reading is "over"
        // and the answer is still to claim nothing — never a negative budget the queue could
        // draw down as runs end.
        ProjectRunCeiling over = new(LiveRuns: 3, Cap: 1);

        over.OverCap.Should().BeTrue();
        over.AtCap.Should().BeTrue();
        over.Admits(claimedThisSweep: 0).Should().BeFalse();
    }
}
