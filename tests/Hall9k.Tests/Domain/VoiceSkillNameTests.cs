using FluentAssertions;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The rule the type exists for: an owner's voice skill is a skill DIRECTORY NAME, referenced by
/// name and never a path — which is what keeps the preference from becoming something that walks
/// out of the two directories it is looked for in.
/// </summary>
public sealed class VoiceSkillNameTests
{
    [Theory]
    [InlineData("my-voice")]
    [InlineData("house_voice")]
    [InlineData("voice2")]
    [InlineData("MyVoice")]
    public void A_skill_directory_name_parses_to_itself(string name) =>
        VoiceSkillName.Parse(name).Value.Should().Be(name);

    [Fact]
    public void Surrounding_whitespace_is_trimmed_rather_than_refused() =>
        VoiceSkillName.Parse("  my-voice  ").Value.Should().Be("my-voice");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_the_honest_absence_rather_than_an_error(string? value)
    {
        VoiceSkillName parsed = VoiceSkillName.Parse(value);

        parsed.Should().Be(VoiceSkillName.None);
        parsed.HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("skills/my-voice")]
    [InlineData(@"C:\Users\me\.claude\skills\my-voice")]
    [InlineData("~/.claude/skills/my-voice")]
    [InlineData("my voice")]
    [InlineData("-leading-hyphen")]
    [InlineData("my.voice")]
    public void Anything_that_is_not_a_directory_name_is_refused(string value)
    {
        Action act = () => VoiceSkillName.Parse(value);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*is not a skill name*");
    }

    [Fact]
    public void A_refusal_quotes_the_input_it_refused_so_the_human_can_see_their_own_typo() =>
        FluentActions.Invoking(() => VoiceSkillName.Parse("skills/my-voice"))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*skills/my-voice*");

    [Fact]
    public void A_paragraph_pasted_in_is_refused_rather_than_recorded() =>
        FluentActions.Invoking(() => VoiceSkillName.Parse(new string('a', 65)))
            .Should().Throw<DomainValidationException>();

    /// <summary>
    /// The one edit this type is allowed to make is trimming. In particular it never rewrites the
    /// case, because a skill directory's name is a filesystem name and case-folding it would name a
    /// directory that does not exist on a case-sensitive filesystem.
    /// </summary>
    [Fact]
    public void Case_is_preserved_because_a_skill_directory_is_a_filesystem_name() =>
        VoiceSkillName.Parse("MyVoice").Value.Should().Be("MyVoice", "not 'myvoice'");
}
