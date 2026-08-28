using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class TaskTypeTests
{
    [Theory]
    [InlineData("pr-review")]
    [InlineData("pr_review")]
    [InlineData("prreview")]
    [InlineData("PR-REVIEW")]
    public void Pr_review_spellings_all_parse_to_the_same_type(string spelling) =>
        TaskType.Parse(spelling).Should().Be(TaskType.PrReview);

    [Fact]
    public void Pr_review_serializes_as_its_own_word()
    {
        ((string)TaskType.PrReview).Should().Be("PrReview");
    }

    [Fact]
    public void An_unknown_type_names_pr_review_among_the_choices()
    {
        Action parse = () => TaskType.Parse("bogus");

        parse.Should().Throw<DomainValidationException>().Which.Message.Should().Contain("pr-review");
    }
}
