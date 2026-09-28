using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The <c>--persona</c> / <c>--clear-personas</c> pair's own shape (idea b9b09779, piece 1):
/// which of the three answers <c>h9k owner set</c> records, what <c>h9k owner show</c> prints
/// back, and the two refusals that never reach the database at all.
/// </summary>
public sealed class ReviewPersonaOptionTests
{
    [Fact]
    public void Neither_option_passed_leaves_the_declaration_alone() =>
        ReviewPersonaOption.Resolve(personas: null, clear: false)
            .Should().Be(Optional<IReadOnlyList<ReviewPersona>>.None);

    [Fact]
    public void A_repeated_option_records_the_whole_set_in_the_fixed_order()
    {
        Optional<IReadOnlyList<ReviewPersona>> resolved =
            ReviewPersonaOption.Resolve(["designer", "engineer"], clear: false);

        resolved.HasValue.Should().BeTrue();
        resolved.Value.Should().Equal(ReviewPersona.Engineer, ReviewPersona.Designer);
    }

    [Fact]
    public void Clear_records_the_honest_absence_rather_than_omitting_the_setting()
    {
        Optional<IReadOnlyList<ReviewPersona>> resolved =
            ReviewPersonaOption.Resolve(personas: null, clear: true);

        resolved.HasValue.Should().BeTrue();
        resolved.Value.Should().BeEmpty();
    }

    [Fact]
    public void Passing_both_options_is_refused_rather_than_one_silently_winning() =>
        FluentActions.Invoking(() => ReviewPersonaOption.Resolve(["qa"], clear: true))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*ask for opposite things*");

    /// <summary>
    /// Refused with the word the human actually typed, rather than dropped into a set that
    /// silently holds one fewer review than they asked for.
    /// </summary>
    [Fact]
    public void An_unrecognized_persona_is_refused_naming_what_was_typed() =>
        FluentActions.Invoking(() => ReviewPersonaOption.Resolve(["qa", "tester"], clear: false))
            .Should().Throw<DomainValidationException>()
            .WithMessage("*tester*");

    [Fact]
    public void An_owner_who_declared_none_reads_as_the_engineer_in_the_pane() =>
        ReviewPersonaOption.Describe([]).Should().Contain("engineer's review");

    [Fact]
    public void A_declaration_prints_its_personas_in_the_pane() =>
        ReviewPersonaOption.Describe([ReviewPersona.Designer, ReviewPersona.Qa])
            .Should().StartWith("qa, designer");
}
