using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The setting itself (task 412afe6c): what an untouched project reads, what an operator's own
/// text records as, and the two things a house style is not allowed to be.
/// </summary>
public sealed class WritingConventionsTests
{
    /// <summary>
    /// The default is not merely "some text": it is the text the acceptance criteria name, and
    /// every prompt in the platform pastes it verbatim, so its content is the contract rather than
    /// an implementation detail.
    /// </summary>
    [Fact]
    public void The_platform_default_states_all_three_house_rules()
    {
        WritingConventions.Default.Value
            .Should().Contain("No em dashes (U+2014)")
            .And.Contain("Full sentences")
            .And.Contain("Generated with Claude")
            .And.Contain("Co-Authored-By");
    }

    /// <summary>Nothing recorded, and 'default' or a blank recorded, come to the same text.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_input_records_the_platform_default(string? input) =>
        WritingConventions.Parse(input).Should().Be(WritingConventions.Default);

    [Fact]
    public void An_operators_own_text_is_recorded_verbatim_and_trimmed()
    {
        WritingConventions parsed = WritingConventions.Parse("  Write like a person. No exclamation marks.  ");

        parsed.Value.Should().Be("Write like a person. No exclamation marks.");
        parsed.Should().NotBe(WritingConventions.Default);
    }

    /// <summary>
    /// Multi-line conventions survive as multi-line prompt text, which is the shape a real house
    /// style takes; the indent is the caller's and every line gets it.
    /// </summary>
    [Fact]
    public void Prompt_lines_carry_every_line_of_the_text_under_the_callers_indent()
    {
        WritingConventions conventions = WritingConventions.Parse("First rule.\nSecond rule.");

        conventions.ToPromptLines("  > ").Should().Be("  > First rule.\n  > Second rule.\n".ReplaceLineEndings());
    }

    [Fact]
    public void A_text_past_the_length_bound_is_refused_at_the_command_line()
    {
        Action act = () => WritingConventions.Parse(new string('x', WritingConventions.MaximumLength + 1));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*past the 4000-character limit*");
    }

    /// <summary>
    /// A layout override is refused, and the refusal names the code point rather than printing the
    /// character, which is the whole reason it is refused.
    /// </summary>
    [Fact]
    public void A_layout_override_character_is_refused_and_named_by_code_point()
    {
        Action act = () => WritingConventions.Parse($"Write plainly.{(char)0x202E}");

        act.Should().Throw<DomainValidationException>().WithMessage("*U+202E*");
    }

    /// <summary>Newlines and tabs are how a real house style is laid out, so they are not that class.</summary>
    [Fact]
    public void Newlines_and_tabs_are_not_illegible()
    {
        Action act = () => WritingConventions.Parse("First rule.\n\tSecond rule.");

        act.Should().NotThrow();
    }
}
