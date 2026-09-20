using FluentAssertions;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A team member declares zero or more review personas on their own member record (idea b9b09779,
/// piece 1): the value object's own rules, and the round trip through
/// <see cref="OwnerDecider.ChangeSettings"/> onto both the aggregate and the projection
/// <c>h9k owner show</c> and the pr-review dispatch actually read.
/// </summary>
public sealed class ReviewPersonaDeclarationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_set_is_fixed_and_a_word_outside_it_is_refused_naming_the_whole_set()
    {
        ReviewPersona.Parse("qa").Should().Be(ReviewPersona.Qa);
        ReviewPersona.Parse(" Designer ").Should().Be(ReviewPersona.Designer);

        Action act = () => ReviewPersona.Parse("tester");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*engineer, qa, designer*");
    }

    [Fact]
    public void Declaring_none_reads_as_the_engineer_so_nothing_changes_for_anyone_who_never_declares_one()
    {
        ReviewPersona.ForReview(null).Should().Equal(ReviewPersona.Engineer);
        ReviewPersona.ForReview([]).Should().Equal(ReviewPersona.Engineer);
    }

    [Fact]
    public void A_declaration_is_normalized_to_the_fixed_order_with_duplicates_and_unknowns_dropped() =>
        ReviewPersona.Declared([ReviewPersona.Designer, ReviewPersona.Qa, ReviewPersona.Designer, ReviewPersona.Unknown])
            .Should().Equal(ReviewPersona.Qa, ReviewPersona.Designer);

    [Fact]
    public void An_owner_declares_no_personas_until_they_say_so() =>
        Registered().ReviewPersonas.Should().BeEmpty();

    [Fact]
    public void Declared_personas_round_trip_through_the_event_onto_the_aggregate()
    {
        OwnerAggregate owner = Registered();

        OwnerSettingsChanged changed = OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now, Optional<VoiceSkillName>.None,
            Optional<IReadOnlyList<ReviewPersona>>.Of([ReviewPersona.Designer, ReviewPersona.Engineer]));
        owner.Apply(changed);

        changed.ReviewPersonas.HasValue.Should().BeTrue();
        // The decider records the canonical set, so every writer's event reads the same way.
        changed.ReviewPersonas.Value.Should().Equal(ReviewPersona.Engineer, ReviewPersona.Designer);
        owner.ReviewPersonas.Should().Equal(ReviewPersona.Engineer, ReviewPersona.Designer);
    }

    /// <summary>
    /// An empty list is a legal explicit value, exactly as <see cref="VoiceSkillName.None"/> is:
    /// it is what <c>--clear-personas</c> records, and it has to be distinguishable from
    /// "not mentioned".
    /// </summary>
    [Fact]
    public void Clearing_the_declaration_is_an_explicit_value_rather_than_an_omission()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now, Optional<VoiceSkillName>.None,
            Optional<IReadOnlyList<ReviewPersona>>.Of([ReviewPersona.Qa])));

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1), Optional<VoiceSkillName>.None,
            Optional<IReadOnlyList<ReviewPersona>>.Of([])));

        owner.ReviewPersonas.Should().BeEmpty();
        ReviewPersona.ForReview(owner.ReviewPersonas).Should().Equal(ReviewPersona.Engineer);
    }

    [Fact]
    public void A_settings_change_that_never_mentions_the_personas_leaves_them_alone()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now, Optional<VoiceSkillName>.None,
            Optional<IReadOnlyList<ReviewPersona>>.Of([ReviewPersona.Qa])));

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now.AddMinutes(1)));

        owner.ReviewPersonas.Should().Equal(ReviewPersona.Qa);
        owner.ReviewRerequest.Should().Be(ReviewRerequestPolicy.Enabled);
    }

    [Fact]
    public void An_unreadable_persona_is_refused_rather_than_silently_dropped()
    {
        OwnerAggregate owner = Registered();

        Action act = () => OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now, Optional<VoiceSkillName>.None,
            Optional<IReadOnlyList<ReviewPersona>>.Of([ReviewPersona.Qa, ReviewPersona.Unknown]));

        act.Should().Throw<DomainValidationException>().WithMessage("*engineer, qa, designer*");
    }

    /// <summary>
    /// The read side both <c>h9k owner show</c> and the pr-review dispatch go through, kept in
    /// step with the aggregate above.
    /// </summary>
    [Fact]
    public void The_projection_carries_the_declaration_the_same_way_the_aggregate_does()
    {
        Guid id = DomainId.New();
        OwnerDetailsProjection projection = new();
        OwnerDetails view = projection.Create(new FakeEvent<OwnerRegistered>(
            new OwnerRegistered(id, "Test Owner", "owner@test.local", Now)));

        view.ReviewPersonas.Should().BeEmpty();

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now, Optional<VoiceSkillName>.None,
                Optional<IReadOnlyList<ReviewPersona>>.Of([ReviewPersona.Designer, ReviewPersona.Qa]))),
            view);

        view.ReviewPersonas.Should().Equal(ReviewPersona.Qa, ReviewPersona.Designer);

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1), Optional<VoiceSkillName>.None,
                Optional<IReadOnlyList<ReviewPersona>>.Of([]))),
            view);

        view.ReviewPersonas.Should().BeEmpty();
    }

    /// <summary>
    /// An Owner stream written before personas existed has no field to read, and must replay as
    /// an owner who declared none rather than as anything else.
    /// </summary>
    [Fact]
    public void An_event_written_before_personas_existed_replays_as_no_declaration()
    {
        OwnerAggregate owner = Registered();

        owner.Apply(new OwnerSettingsChanged(
            owner.Id, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now));

        owner.ReviewPersonas.Should().BeEmpty();
    }

    private static OwnerAggregate Registered()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Test Owner", "owner@test.local", Now));
        return owner;
    }
}
