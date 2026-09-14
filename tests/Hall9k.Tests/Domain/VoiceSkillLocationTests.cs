using FluentAssertions;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The two tiers a named voice skill is looked for in, and the refusal when it is in neither.
/// Every fact here runs against scratch directories rather than the operator's own live Claude
/// Code state, through the parameterised overload that exists for exactly that reason.
/// </summary>
public sealed class VoiceSkillLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"h9k-voice-skill-{Guid.NewGuid():N}");
    private readonly string _userSkills;
    private readonly string _projectHome;

    public VoiceSkillLocationTests()
    {
        _userSkills = Path.Combine(_root, "user", ".claude", "skills");
        _projectHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(_userSkills);
        Directory.CreateDirectory(ProjectHomePaths.SkillsDirectory(_projectHome));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_skill_in_the_owners_own_user_skills_resolves_there()
    {
        WriteSkill(_userSkills, "my-voice");

        string resolved = VoiceSkillLocation.Resolve(
            VoiceSkillName.Parse("my-voice"), _userSkills, [_projectHome]);

        resolved.Should().Be(Path.Combine(_userSkills, "my-voice"));
    }

    [Fact]
    public void A_skill_in_a_project_homes_skills_resolves_there()
    {
        WriteSkill(ProjectHomePaths.SkillsDirectory(_projectHome), "team-voice");

        string resolved = VoiceSkillLocation.Resolve(
            VoiceSkillName.Parse("team-voice"), _userSkills, [_projectHome]);

        resolved.Should().Be(Path.Combine(ProjectHomePaths.SkillsDirectory(_projectHome), "team-voice"));
    }

    /// <summary>
    /// Least specific first, the same order the skill tiers already resolve in (Decisions Log #76):
    /// the owner's own is the voice they carry between projects, so it is the one that answers when
    /// both tiers have a skill of the same name.
    /// </summary>
    [Fact]
    public void The_owners_own_user_skill_wins_when_both_tiers_hold_the_name()
    {
        WriteSkill(_userSkills, "my-voice");
        WriteSkill(ProjectHomePaths.SkillsDirectory(_projectHome), "my-voice");

        VoiceSkillLocation.Resolve(VoiceSkillName.Parse("my-voice"), _userSkills, [_projectHome])
            .Should().Be(Path.Combine(_userSkills, "my-voice"));
    }

    [Fact]
    public void A_name_in_neither_tier_is_refused_naming_both_paths_it_looked_in()
    {
        Action act = () => VoiceSkillLocation.Resolve(
            VoiceSkillName.Parse("my-voice"), _userSkills, [_projectHome]);

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should()
                .Contain(Path.Combine(_userSkills, "my-voice"))
                .And.Contain(Path.Combine(ProjectHomePaths.SkillsDirectory(_projectHome), "my-voice"))
                .And.Contain("No voice skill named 'my-voice'");
    }

    /// <summary>
    /// A directory is not a skill: <c>SKILL.md</c> is the discriminator everywhere else in this
    /// codebase, and a name pointing at an empty directory would record a preference no session
    /// could ever load.
    /// </summary>
    [Fact]
    public void A_bare_directory_with_no_SKILL_file_is_not_a_skill()
    {
        Directory.CreateDirectory(Path.Combine(_userSkills, "my-voice"));

        FluentActions.Invoking(() => VoiceSkillLocation.Resolve(
                VoiceSkillName.Parse("my-voice"), _userSkills, [_projectHome]))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*no SKILL.md is there*");
    }

    /// <summary>
    /// An owner with no project home registered has exactly one tier, and the refusal says so
    /// rather than naming a second path it never checked (AGENTS.md's never-guess rule).
    /// </summary>
    [Fact]
    public void With_no_project_home_the_refusal_names_the_one_tier_it_had_and_says_why()
    {
        Action act = () => VoiceSkillLocation.Resolve(VoiceSkillName.Parse("my-voice"), _userSkills, []);

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should()
                .Contain(Path.Combine(_userSkills, "my-voice"))
                .And.Contain("no project home is registered to this owner yet");
    }

    [Fact]
    public void Candidate_directories_are_the_user_tier_then_every_project_home_in_order()
    {
        string second = Path.Combine(_root, "home-2");

        VoiceSkillLocation.CandidateDirectories(
                VoiceSkillName.Parse("my-voice"), _userSkills, [_projectHome, second])
            .Should().Equal(
                Path.Combine(_userSkills, "my-voice"),
                Path.Combine(ProjectHomePaths.SkillsDirectory(_projectHome), "my-voice"),
                Path.Combine(ProjectHomePaths.SkillsDirectory(second), "my-voice"));
    }

    private static void WriteSkill(string skillsDirectory, string name)
    {
        string skill = Path.Combine(skillsDirectory, name);
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: {name}\ndescription: a voice.\n---\n");
    }
}
