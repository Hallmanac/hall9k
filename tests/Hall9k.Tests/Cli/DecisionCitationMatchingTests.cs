using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The half of <c>h9k decide show</c>'s reference resolution that answers a citation from before
/// this store (idea d805fd8b, piece 3). Driven as a pure function over projection rows rather than
/// through a store, because what is worth proving here is which spellings name an entry and which
/// are refused, and the query in front of it is the same query every other resolver uses.
/// </summary>
public sealed class DecisionCitationMatchingTests
{
    private static readonly DecisionDetails Numbering = Imported("Decisions Log #162");
    private static readonly DecisionDetails First = Imported("Decisions Log #1");
    private static readonly DecisionDetails GitRule = Imported("AGENTS.md Git rules #1");
    private static readonly DecisionDetails Native = Imported(null);

    private static readonly IReadOnlyList<DecisionDetails> All = [Numbering, First, GitRule, Native];

    [Fact]
    public void The_whole_citation_names_its_decision()
    {
        Match("Decisions Log #162").Should().Equal(Numbering.Id);
        Match("AGENTS.md Git rules #1").Should().Equal(GitRule.Id);
    }

    /// <summary>
    /// The spellings the repository actually carries. A §16 entry is cited by its section in a
    /// hundred-odd source comments, and those were never rewritten into the log's own name, so
    /// the number alone has to be enough.
    /// </summary>
    [Theory]
    [InlineData("§16 #162")]
    [InlineData("PLAN.md §16 #162")]
    [InlineData("#162")]
    [InlineData("decisions log #162")]
    public void A_section_spelling_of_the_same_number_names_the_same_decision(string citation) =>
        Match(citation).Should().Equal(Numbering.Id);

    /// <summary>
    /// A number several sections each have an entry for is genuinely ambiguous, and the caller
    /// reports it rather than picking: #1 belongs to the log's first entry and to AGENTS.md's
    /// first Git rule both.
    /// </summary>
    [Fact]
    public void A_number_two_sections_share_comes_back_as_both()
    {
        Match("#1").Should().BeEquivalentTo(new[] { First.Id, GitRule.Id });
        Match("Decisions Log #1").Should().Equal([First.Id], "the whole citation names one of them");
    }

    /// <summary>
    /// The guard that keeps this out of the id fragment's way. A bare number is the front of an
    /// id, which is what a reader copies out of <c>h9k decide list</c>, and reading it as a
    /// citation as well would make one of the two unreachable.
    /// </summary>
    [Fact]
    public void A_number_without_a_hash_is_not_a_citation()
    {
        Match("162").Should().BeEmpty();
        Match("1").Should().BeEmpty();
    }

    [Fact]
    public void Nothing_matches_a_number_no_decision_kept() =>
        Match("Decisions Log #9999").Should().BeEmpty();

    /// <summary>
    /// The number carries a citation whose section name nothing recorded, which is deliberate
    /// rather than tolerated: the spellings in the wild are unbounded — this repository's own docs
    /// cite the same log as "§16 #62", "PLAN.md §16 #62" and "log #66" — so a section name is read
    /// as context and the number is what resolves. The whole citation is still tried first, so
    /// this only ever fires for a name that matched nothing, and a number several sections share
    /// comes back ambiguous rather than picked between.
    /// </summary>
    [Fact]
    public void A_section_name_nothing_recorded_still_resolves_by_its_number()
    {
        Match("log #162").Should().Equal([Numbering.Id]);
        Match("Some section nobody has #9999").Should().BeEmpty();
    }

    /// <summary>
    /// A prefix of a citation is not that citation. Matching loosely here would resolve
    /// "Decisions Log" to whichever entry happened to sort first, which is worse than saying no.
    /// </summary>
    [Fact]
    public void A_citation_is_never_resolved_by_a_prefix_of_one()
    {
        Match("Decisions Log").Should().BeEmpty();
        Match("AGENTS.md").Should().BeEmpty();
    }

    [Fact]
    public void Whitespace_around_and_inside_a_citation_does_not_change_which_one_it_is() =>
        Match("  Decisions Log   #162 ").Should().Equal(Numbering.Id);

    private static Guid[] Match(string text) => DecisionIdResolver.CitationMatches(All, text);

    private static DecisionDetails Imported(string? legacyId) =>
        new() { Id = DomainId.New(), LegacyId = legacyId, Statement = $"Whatever {legacyId} said." };
}
