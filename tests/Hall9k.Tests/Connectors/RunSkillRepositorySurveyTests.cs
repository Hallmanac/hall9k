using FluentAssertions;
using Hall9k.Connectors.RunSkills;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The mechanical half of run-skill discovery (idea b9b09779, piece 4), against a real temp
/// directory rather than a filesystem fake: the whole point of this component is what it finds on
/// disk, so a fake would be asserting its own opinion of a directory listing.
/// </summary>
public sealed class RunSkillRepositorySurveyTests : IDisposable
{
    private readonly string _repository =
        Path.Combine(Path.GetTempPath(), $"hall9k-survey-{Guid.NewGuid():N}");

    public RunSkillRepositorySurveyTests() => Directory.CreateDirectory(_repository);

    public void Dispose()
    {
        if (Directory.Exists(_repository))
        {
            Directory.Delete(_repository, recursive: true);
        }
    }

    [Fact]
    public void A_directory_that_does_not_exist_surveys_as_empty_rather_than_throwing()
    {
        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(Path.Combine(_repository, "never-cloned"));

        survey.HasAnything.Should().BeFalse();
        survey.All.Should().BeEmpty();
    }

    [Fact]
    public void A_repository_with_only_source_in_it_has_nothing_the_survey_can_read()
    {
        Write(Path.Combine("src", "Thing.cs"), "class Thing;");

        RunSkillRepositorySurvey.Survey(_repository).HasAnything.Should().BeFalse();
    }

    [Fact]
    public void A_README_whose_headings_mention_launching_is_launch_coverage_and_one_that_does_not_is_documentation()
    {
        Write("README.md", "# Thing\n\n## Running it locally\n\n`make dev`\n");

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(_repository);

        survey.LaunchCoverage.Should().ContainSingle().Which.Path.Should().Be("README.md");
        survey.Documentation.Should().BeEmpty();

        File.WriteAllText(Path.Combine(_repository, "README.md"), "# Thing\n\n## What it is\n\nA thing.\n");
        RunSkillSurvey plain = RunSkillRepositorySurvey.Survey(_repository);

        plain.LaunchCoverage.Should().BeEmpty();
        plain.Documentation.Should().ContainSingle().Which.Path.Should().Be("README.md");
    }

    [Fact]
    public void A_briefing_whose_casing_differs_is_still_found_and_cited_by_its_real_name()
    {
        // Pattern-matched enumeration is case-sensitive on Linux, so a repository carrying
        // Readme.md would have gone unread there — and a run skill citing README.md would send
        // its reader to a path that is not on disk.
        Write("Readme.md", "# Thing\n\n## How to run it\n\n`make dev`\n");

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(_repository);

        survey.LaunchCoverage.Should().ContainSingle().Which.Path.Should().Be("Readme.md");
    }

    [Fact]
    public void A_build_manifest_is_never_listed_twice_however_many_rules_reach_it()
    {
        Write("Hall9k.slnx", "<Solution />");
        Write("Makefile", "dev:\n");

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(_repository);

        survey.BuildFiles.Select(found => found.Path).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_passing_mention_of_running_outside_a_heading_is_not_launch_coverage()
    {
        // Headings only, deliberately: a README that says "run the tests" mid-paragraph has not
        // documented how to launch anything, and counting it would make the hint worthless.
        Write("README.md", "# Thing\n\n## What it is\n\nYou can run the tests with dotnet test.\n");

        RunSkillRepositorySurvey.Survey(_repository).LaunchCoverage.Should().BeEmpty();
    }

    [Fact]
    public void Every_repository_skill_is_a_launch_coverage_candidate_whatever_it_is_called()
    {
        Write(Path.Combine(".claude", "skills", "commit-plan", "SKILL.md"), "Organize commits.");
        Write(Path.Combine(".claude", "skills", "run", "SKILL.md"), "Launch it.");
        Write(Path.Combine(".claude", "skills", "not-a-skill", "NOTES.md"), "no SKILL.md here");

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(_repository);

        survey.LaunchCoverage.Select(found => found.Path).Should().BeEquivalentTo(
            [".claude/skills/commit-plan/SKILL.md", ".claude/skills/run/SKILL.md"]);
    }

    [Fact]
    public void A_docs_page_whose_name_reads_like_a_launch_guide_is_launch_coverage_and_the_rest_is_documentation()
    {
        Write(Path.Combine("docs", "getting-started.md"), "Start here.");
        Write(Path.Combine("docs", "architecture.md"), "How it is shaped.");

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(_repository);

        survey.LaunchCoverage.Select(found => found.Path).Should().Equal("docs/getting-started.md");
        survey.Documentation.Select(found => found.Path).Should().Equal("docs/architecture.md");
    }

    [Fact]
    public void Build_manifests_are_found_by_name_and_by_the_globs_whose_filename_varies()
    {
        Write("docker-compose.yml", "services:\n");
        Write("Hall9k.slnx", "<Solution />");
        Write("notes.txt", "not a build file");

        RunSkillSurvey survey = RunSkillRepositorySurvey.Survey(_repository);

        survey.BuildFiles.Select(found => found.Path).Should().BeEquivalentTo(["docker-compose.yml", "Hall9k.slnx"]);
        survey.HasAnything.Should().BeTrue("a build file alone is enough to be worth a session's read");
    }

    [Fact]
    public void What_the_survey_looked_for_names_every_place_it_actually_checks()
    {
        // The none-discoverable skill prints this list, and its whole job is letting a human tell
        // "there is nothing here" apart from "the platform looked in the wrong place".
        string all = string.Join('\n', RunSkillRepositorySurvey.LookedFor);

        all.Should().Contain("README.md");
        all.Should().Contain("AGENTS.md");
        all.Should().Contain("CLAUDE.md");
        all.Should().Contain("docs/");
        all.Should().Contain(".claude/skills/*/SKILL.md");
        all.Should().Contain("docker-compose.yml");
    }

    private void Write(string relativePath, string content)
    {
        string file = Path.Combine(_repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }
}
