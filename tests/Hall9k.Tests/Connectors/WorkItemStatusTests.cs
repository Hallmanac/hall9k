using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The two states Hall9k has a rule for, and the much larger set it does not. The adoption gate
/// reads <see cref="WorkItemStatus.IsOpen"/> positively (PLAN.md §3.1a), so everything here is
/// really one question: can a state nobody mapped ever be mistaken for a source saying open?
/// </summary>
public sealed class WorkItemStatusTests
{
    /// <summary>
    /// "open" and "closed" are in the theory because they are the words that make the mistake
    /// possible: an adapter reaches <see cref="WorkItemStatus.Unmapped"/> having established that
    /// it could not tell, and a status whose name happens to be spelled like Hall9k's own word
    /// for open is still nobody saying so. Origin incident (2026-08-22): Unmapped recorded the
    /// observed word as the mapped value, so Unmapped("open") was equal to Open and read as open,
    /// which is the guess the Jira adapter's no-category fallback exists to refuse.
    /// </summary>
    [Theory]
    [InlineData("open")]
    [InlineData("Open")]
    [InlineData("OPEN")]
    [InlineData("closed")]
    [InlineData("In Review")]
    [InlineData("Ready for Ozzie")]
    public void A_state_nobody_mapped_never_reads_as_open(string observed)
    {
        WorkItemStatus status = WorkItemStatus.Unmapped(observed);

        status.IsOpen.Should().BeFalse("the adapter said it could not map this, whatever the word is");
        status.Should().NotBe(WorkItemStatus.Open);
        status.Should().NotBe(WorkItemStatus.Closed);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("Bespoke")]
    public void A_state_nobody_mapped_still_says_what_was_observed(string observed)
    {
        WorkItemStatus.Unmapped(observed).ToString().Should().Be($"{observed} (unknown)");
    }

    [Fact]
    public void A_state_the_source_left_out_is_unknown_rather_than_an_empty_observation()
    {
        WorkItemStatus.Unmapped(null).Should().Be(WorkItemStatus.Unknown);
        WorkItemStatus.Unmapped("   ").Should().Be(WorkItemStatus.Unknown);
        WorkItemStatus.Unknown.ToString().Should().Be("unknown");
    }

    /// <summary>
    /// A source whose own word for a state it could not map is "unknown" is recorded as that one
    /// word, for the same reason "open (open)" cannot happen: the observation and the reading are
    /// only worth printing separately when they differ.
    /// </summary>
    [Fact]
    public void An_observation_that_reads_like_its_own_mapping_is_printed_once()
    {
        WorkItemStatus.Unmapped("Unknown").ToString().Should().Be("unknown");
        WorkItemStatus.Open.As("open").ToString().Should().Be("open");
    }

    /// <summary>
    /// The other half of the boundary: a source whose vocabulary <em>is</em> Hall9k's goes through
    /// <see cref="WorkItemStatus.Parse"/>, which is the only way a state reads as open.
    /// </summary>
    [Fact]
    public void A_source_whose_own_word_is_open_is_parsed_as_open()
    {
        WorkItemStatus.Parse("OPEN").Should().Be(WorkItemStatus.Open);
        WorkItemStatus.Parse(" closed ").Should().Be(WorkItemStatus.Closed);
        WorkItemStatus.Parse("").Should().Be(WorkItemStatus.Unknown);
        WorkItemStatus.Parse("In Review").IsOpen.Should().BeFalse();
    }

    /// <summary>
    /// A mapped state carries the board's own word alongside it, because the agent context stamps
    /// what was observed and the gate reads what was mapped, and neither may stand in for the
    /// other (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    [Fact]
    public void A_mapped_state_keeps_the_word_the_board_used_for_it()
    {
        WorkItemStatus status = WorkItemStatus.Open.As("In Progress");

        status.IsOpen.Should().BeTrue();
        status.SourceLabel.Should().Be("In Progress");
        status.ToString().Should().Be("In Progress (open)");
    }
}
