using FluentAssertions;
using Hall9k.Connectors.RunSkills;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// What the run-skill discovery prompt actually tells a session (idea b9b09779, piece 4). The
/// three things asserted here are the ones the acceptance criteria put on the prompt rather than
/// on any parseable output: the six shared sections, the never-guess rule for anything the
/// session could not determine, and the pointer-or-full-text call being the session's own.
/// </summary>
public sealed class RunSkillDiscoveryPromptTests
{
    private static ProjectDetails Project() => new()
    {
        Id = DomainId.New(),
        Name = "hall9k",
        BaseBranch = "main",
        RepositoryPath = "/repo/hall9k.git",
    };

    private static string Build(RunSkillSurvey survey) => AgentPromptBuilder.BuildRunSkillDiscovery(
        Project(), "/repo/dev", "abc1234", survey, ProjectDecider.RunSkillMaximumLength);

    [Fact]
    public void The_prompt_names_every_shared_section_in_order()
    {
        string prompt = Build(new RunSkillSurvey([], [], []));

        int previous = -1;
        foreach (string heading in RunSkillDocument.Headings)
        {
            int at = prompt.IndexOf($"`## {heading}`", StringComparison.Ordinal);
            at.Should().BeGreaterThan(previous, $"'{heading}' must appear, after the section before it");
            previous = at;
        }
    }

    [Fact]
    public void The_prompt_states_the_never_guess_rule_and_where_an_undetermined_thing_goes()
    {
        string prompt = Build(new RunSkillSurvey([], [], []));

        prompt.Should().Contain("never guessed at");
        prompt.Should().Contain("A plausible-looking value you invented is worse than an admitted gap");
        prompt.Should().Contain("A secret or credential you have no value for.");
        prompt.Should().Contain("A service you");
    }

    [Fact]
    public void The_prompt_leaves_the_shape_to_the_session_and_says_what_each_one_means()
    {
        string prompt = Build(new RunSkillSurvey([], [], []));

        prompt.Should().Contain("**pointer**");
        prompt.Should().Contain("**full-text**");
        prompt.Should().Contain("is your call, made from the files, not from the scan's guess");
        prompt.Should().Contain(AgentPromptBuilder.RunSkillShapeMarker);
        prompt.Should().Contain(AgentPromptBuilder.RunSkillMarkdownMarker);
        prompt.Should().NotContain(
            RunSkillShape.NoneDiscoverable.Value,
            "the none-discoverable outcome is the daemon's own, decided before a session ever runs");
    }

    [Fact]
    public void The_survey_is_rendered_by_bucket_and_an_empty_bucket_says_so_rather_than_vanishing()
    {
        string prompt = Build(new RunSkillSurvey(
            [new RunSkillEvidence("docs/running.md", "a documentation file whose own name reads like a launch guide")],
            [],
            [new RunSkillEvidence("Makefile", "a build or run manifest at the repository root")]));

        prompt.Should().Contain("- `docs/running.md` — a documentation file whose own name reads like a launch guide");
        prompt.Should().Contain("- `Makefile` — a build or run manifest at the repository root");
        prompt.Should().Contain("Other documentation the scan found:");
        prompt.Should().Contain("(nothing in this category)");
    }

    [Fact]
    public void The_prompt_is_read_only_and_never_asks_the_session_to_write_anything_anywhere()
    {
        string prompt = Build(new RunSkillSurvey([], [], []));

        prompt.Should().Contain("This session is read-only");
        prompt.Should().Contain("no ledger for you to write");
        prompt.Should().Contain("Commit this composition is against: `abc1234`");
    }
}
