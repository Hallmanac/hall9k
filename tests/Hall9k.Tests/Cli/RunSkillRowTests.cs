using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The line <c>h9k project show</c> prints for the run skill (idea b9b09779, piece 4). Every case
/// here is about telling absences apart: a repository genuinely without a discoverable way to run
/// it is a finding somebody established, and it must never read the same as nobody having looked.
/// </summary>
public sealed class RunSkillRowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static ProjectDetails Project() => new() { Id = DomainId.New(), Name = "hall9k" };

    private static ProjectRunSkill Skill(RunSkillShape shape, RunSkillAuthor author) =>
        new("Run skill shape: whatever.\n\n## Launch\n\nmake dev\n", shape, "abc123", author, Now, DomainId.New());

    [Fact]
    public void Nobody_having_asked_reads_as_none_and_names_the_command()
    {
        ProjectShowCommand.RunSkillRow(Project())
            .Should().Contain("none — ask for one").And.Contain("--discover-run-skill");
    }

    [Fact]
    public void An_outstanding_request_says_the_daemon_is_coming_for_it()
    {
        ProjectDetails project = Project();
        project.RunSkillDiscoveryRequestedAt = Now;

        ProjectShowCommand.RunSkillRow(project).Should().Contain("being discovered");
    }

    [Fact]
    public void A_dispatched_discovery_with_nothing_behind_it_names_both_readings_rather_than_asserting_one()
    {
        // Two states leave exactly this shape and the timestamps cannot tell them apart: a
        // session composing right now (the sweep spawns and waits inline, for as long as
        // RunSkillDiscoveryTimeout allows) and one lost mid-wait to a daemon restart. Only the
        // second is worth asking again for, and it is never redispatched on its own — so the row
        // names both rather than asserting the unobserved one, and never promises a sweep.
        ProjectDetails project = Project();
        project.RunSkillDiscoveryRequestedAt = Now;
        project.RunSkillDiscoveryDispatchedAt = Now.AddMinutes(1);

        project.RunSkillDiscoveryOutstanding.Should().BeFalse();
        string row = ProjectShowCommand.RunSkillRow(project);

        row.Should().Contain("recorded nothing since");
        row.Should().Contain("still composing");
        row.Should().Contain("lost mid-wait");
        row.Should().Contain("--discover-run-skill");
        row.Should().NotContain("next run-skill sweep");
    }

    [Fact]
    public void A_failed_discovery_names_its_reason_and_is_not_the_none_discoverable_finding()
    {
        ProjectDetails project = Project();
        project.RunSkillDiscoveryFailure = "the discovery session exceeded its bound";

        string row = ProjectShowCommand.RunSkillRow(project);

        row.Should().Contain("discovery failed");
        row.Should().Contain("exceeded its bound");
        row.Should().NotContain("none discoverable");
    }

    [Fact]
    public void None_discoverable_is_a_finding_and_reads_differently_from_every_absence()
    {
        ProjectDetails project = Project();
        project.RunSkill = Skill(RunSkillShape.NoneDiscoverable, RunSkillAuthor.Platform);

        string row = ProjectShowCommand.RunSkillRow(project);

        row.Should().Contain("none discoverable");
        row.Should().Contain("nothing in this repository says how to run it");
        row.Should().Contain("h9k project run-skill show hall9k");
    }

    [Fact]
    public void A_composed_skill_names_its_shape_and_its_author()
    {
        ProjectDetails project = Project();
        project.RunSkill = Skill(RunSkillShape.Pointer, RunSkillAuthor.DiscoverySession);

        string row = ProjectShowCommand.RunSkillRow(project);

        row.Should().StartWith("pointer");
        row.Should().Contain("composed by discovery-session");
    }

    [Fact]
    public void A_fresh_discovery_over_an_existing_skill_is_announced_beside_it()
    {
        ProjectDetails project = Project();
        project.RunSkill = Skill(RunSkillShape.FullText, RunSkillAuthor.Hand);
        project.RunSkillDiscoveryRequestedAt = Now.AddDays(1);

        ProjectShowCommand.RunSkillRow(project).Should().Contain("a fresh discovery is outstanding");
    }

    [Fact]
    public void A_failed_rediscovery_is_reported_beside_the_skill_it_did_not_replace()
    {
        // A project whose launch story changed keeps its old skill when the re-discovery fails,
        // and a row that printed that skill with no trailer had the owner reading a stale
        // document as current while waiting for a replacement nothing was going to send.
        ProjectDetails project = Project();
        project.RunSkill = Skill(RunSkillShape.Pointer, RunSkillAuthor.DiscoverySession);
        project.RunSkillDiscoveryFailure = "the discovery session exceeded its bound";

        string row = ProjectShowCommand.RunSkillRow(project);

        row.Should().StartWith("pointer");
        row.Should().Contain("the last discovery failed");
        row.Should().Contain("exceeded its bound");
        row.Should().Contain("--discover-run-skill");
    }
}
