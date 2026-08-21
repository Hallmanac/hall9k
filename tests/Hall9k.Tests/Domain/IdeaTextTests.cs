using FluentAssertions;
using Hall9k.Domain.Features.Idea;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The promotion split is mechanical and visible (Decisions Log #35): the first sentence
/// becomes the draft's objective and the rest becomes its context, with no attempt to
/// understand either. These cases pin the mechanics — including the ones where a purely
/// mechanical rule splits somewhere a human would not, which is why --objective exists.
/// </summary>
public sealed class IdeaTextTests
{
    [Fact]
    public void The_first_sentence_becomes_the_objective_and_the_rest_becomes_context()
    {
        IdeaSeed seed = IdeaText.Seed(
            "Give every idea a discovery workspace. Research notes and prototypes need somewhere to live.");

        seed.Objective.Should().Be("Give every idea a discovery workspace.");
        seed.Context.Should().Be("Research notes and prototypes need somewhere to live.");
    }

    [Fact]
    public void A_line_break_ends_the_first_sentence_too()
    {
        IdeaSeed seed = IdeaText.Seed("Ideas are first-class\nCapture has to cost nothing.");

        seed.Objective.Should().Be("Ideas are first-class");
        seed.Context.Should().Be("Capture has to cost nothing.");
    }

    [Fact]
    public void A_single_sentence_becomes_the_whole_objective_with_no_context()
    {
        IdeaSeed seed = IdeaText.Seed("  Stacked PRs for dependency chains  ");

        seed.Objective.Should().Be("Stacked PRs for dependency chains");
        seed.Context.Should().BeNull("inventing context out of nothing would be a guess");
    }

    [Theory]
    [InlineData("Does the daemon need this? Probably not.", "Does the daemon need this?", "Probably not.")]
    [InlineData("Ship it! Then measure.", "Ship it!", "Then measure.")]
    public void Question_marks_and_exclamation_points_end_a_sentence_as_written(
        string text, string objective, string context)
    {
        IdeaSeed seed = IdeaText.Seed(text);

        seed.Objective.Should().Be(objective);
        seed.Context.Should().Be(context);
    }

    [Fact]
    public void A_decimal_point_does_not_end_a_sentence_because_no_whitespace_follows_it()
    {
        IdeaSeed seed = IdeaText.Seed("Pin Marten 8.17 before the next slice. It moves fast.");

        seed.Objective.Should().Be("Pin Marten 8.17 before the next slice.");
        seed.Context.Should().Be("It moves fast.");
    }

    [Fact]
    public void An_abbreviation_splits_early_which_is_what_mechanical_means()
    {
        // Documented, not desirable: understanding "e.g." would mean interpreting the note, and
        // the human is right there with --objective. The behaviour is pinned so a later "fix"
        // is a deliberate decision rather than a drift.
        IdeaSeed seed = IdeaText.Seed("Support more providers, e.g. Azure DevOps and GitLab.");

        seed.Objective.Should().Be("Support more providers, e.g.");
        seed.Context.Should().Be("Azure DevOps and GitLab.");
    }

    [Fact]
    public void An_empty_note_seeds_nothing_rather_than_something_invented()
    {
        IdeaSeed seed = IdeaText.Seed("   ");

        seed.Objective.Should().BeEmpty();
        seed.Context.Should().BeNull();
    }
}
