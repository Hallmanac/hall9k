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

    /// <summary>
    /// The non-refusing read, for a caller with somewhere honest to fall back to: a task record
    /// written by a later build can name a type this one has never heard of, and that record
    /// degrades to the fields this build understands rather than failing the whole adoption.
    /// </summary>
    [Fact]
    public void TryParse_answers_false_for_a_word_this_build_does_not_know()
    {
        TaskType.TryParse("bogus", out TaskType? parsed).Should().BeFalse();
        parsed.Should().BeNull("and never Unknown, which would read as a type the record stated");
    }

    [Theory]
    [InlineData("", "Feature")]
    [InlineData("bug", "Bugfix")]
    [InlineData("Research", "Research")]
    [InlineData("PR-REVIEW", "PrReview")]
    public void TryParse_reads_every_word_Parse_reads(string spelling, string expected)
    {
        TaskType.TryParse(spelling, out TaskType? parsed).Should().BeTrue();
        parsed!.Value.Should().Be(expected);
    }
}
