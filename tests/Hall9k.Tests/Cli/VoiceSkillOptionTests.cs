using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The <c>--voice-skill</c> / <c>--clear-voice-skill</c> pair's own shape: which of the three
/// answers <c>h9k owner set</c> records (leave it alone, forget it, record this name), and the two
/// refusals that never reach the database at all.
/// </summary>
public sealed class VoiceSkillOptionTests
{
    [Fact]
    public void Neither_option_passed_leaves_the_preference_alone() =>
        VoiceSkillOption.Resolve(voiceSkill: null, clear: false)
            .Should().Be(Optional<VoiceSkillName>.None);

    [Fact]
    public void A_name_is_recorded_as_an_explicit_value()
    {
        Optional<VoiceSkillName> resolved = VoiceSkillOption.Resolve("my-voice", clear: false);

        resolved.HasValue.Should().BeTrue();
        resolved.Value!.Value.Should().Be("my-voice");
    }

    /// <summary>
    /// Clearing is an explicit None rather than an omission, which is the distinction
    /// <see cref="Optional{T}"/> exists to carry: an omission leaves whatever is recorded, this
    /// forgets it.
    /// </summary>
    [Fact]
    public void Clear_records_the_honest_absence_rather_than_omitting_the_setting()
    {
        Optional<VoiceSkillName> resolved = VoiceSkillOption.Resolve(voiceSkill: null, clear: true);

        resolved.HasValue.Should().BeTrue();
        resolved.Value.Should().Be(VoiceSkillName.None);
    }

    [Fact]
    public void Passing_both_options_is_refused_rather_than_one_silently_winning() =>
        FluentActions.Invoking(() => VoiceSkillOption.Resolve("my-voice", clear: true))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*ask for opposite things*");

    /// <summary>
    /// An empty <c>--voice-skill</c> (an unset shell variable, most likely) is refused rather than
    /// read as a request to clear: the one command that forgets the preference is the one named for
    /// it.
    /// </summary>
    [Fact]
    public void An_empty_name_is_refused_and_points_at_the_clearing_switch() =>
        FluentActions.Invoking(() => VoiceSkillOption.Resolve("   ", clear: false))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*--clear-voice-skill*");

    [Fact]
    public void The_show_pane_names_the_skill_when_one_is_named() =>
        VoiceSkillOption.Describe(VoiceSkillName.Parse("my-voice"))
            .Should().StartWith("my-voice")
            .And.Contain("loaded by name");

    [Fact]
    public void The_show_pane_says_plainly_when_no_skill_is_named() =>
        VoiceSkillOption.Describe(VoiceSkillName.None).Should().Contain("none named");
}
