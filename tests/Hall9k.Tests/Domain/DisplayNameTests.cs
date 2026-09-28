using FluentAssertions;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The rule the type exists for (task e6744304): a display name is trimmed, blank clears it, and
/// otherwise it is 1 to 64 characters with no control characters (a newline, a carriage return,
/// and a tab included), since <c>ExtractQuotedYamlValue</c>'s own line-based reader could not survive
/// one embedded in a node file's value.
/// </summary>
public sealed class DisplayNameTests
{
    [Theory]
    [InlineData("Ada Lovelace")]
    [InlineData("A")]
    [InlineData("田中太郎")]
    public void A_well_formed_name_parses_to_its_trimmed_self(string name) =>
        DisplayName.Parse(name).Value.Should().Be(name);

    [Fact]
    public void Surrounding_whitespace_is_trimmed_rather_than_refused() =>
        DisplayName.Parse("  Ada Lovelace  ").Value.Should().Be("Ada Lovelace");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_is_the_honest_absence_rather_than_an_error(string? value)
    {
        DisplayName parsed = DisplayName.Parse(value);

        parsed.Should().Be(DisplayName.None);
        parsed.HasValue.Should().BeFalse();
    }

    [Fact]
    public void Exactly_64_characters_is_accepted() =>
        DisplayName.Parse(new string('a', 64)).Value.Should().HaveLength(64);

    [Theory]
    [InlineData("line one\nline two")]
    [InlineData("carriage\rreturn")]
    [InlineData("a\ttab")]
    public void A_control_character_is_refused(string value)
    {
        Action act = () => DisplayName.Parse(value);

        act.Should().Throw<DomainValidationException>().WithMessage("*is not a display name*");
    }

    [Fact]
    public void A_refusal_echoes_the_value_with_control_characters_made_visible() =>
        FluentActions.Invoking(() => DisplayName.Parse("Ada\nLovelace"))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*Ada?Lovelace*");

    [Fact]
    public void A_refusal_on_an_overlong_name_still_echoes_it() =>
        FluentActions.Invoking(() => DisplayName.Parse(new string('a', 65)))
            .Should().Throw<DomainValidationException>()
            .WithMessage($"*{new string('a', 64)}*");

    [Fact]
    public void Trusted_wraps_an_already_recorded_value_with_no_re_validation() =>
        DisplayName.Trusted("Ada Lovelace").Value.Should().Be("Ada Lovelace");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Trusted_of_nothing_is_none(string? value) => DisplayName.Trusted(value).Should().Be(DisplayName.None);
}
