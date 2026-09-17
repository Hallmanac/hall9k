using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class PromptBuilderKeyTests
{
    [Theory]
    [InlineData("work")]
    [InlineData("review-lap")]
    [InlineData("agent")]
    [InlineData("mention-follow-up")]
    public void Parse_accepts_every_known_builder(string value)
    {
        PromptBuilderKey.Parse(value).Value.Should().Be(value);
    }

    [Fact]
    public void Parse_refuses_an_unknown_builder_and_names_the_known_ones()
    {
        Action act = () => PromptBuilderKey.Parse("qa-review");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*qa-review*")
            .Where(exception => exception.Message.Contains("work") && exception.Message.Contains("agent"));
    }

    [Fact]
    public void Parse_refuses_a_blank_value()
    {
        Action act = () => PromptBuilderKey.Parse("  ");

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void All_never_contains_the_unknown_sentinel()
    {
        PromptBuilderKey.All.Should().NotContain(PromptBuilderKey.Unknown);
    }
}
