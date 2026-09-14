using FluentAssertions;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class OwnerDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClaimRoot_produces_an_event_carrying_the_fingerprint_and_whether_it_is_verified()
    {
        OwnerAggregate owner = Registered();

        OwnerRootClaimed claimed = OwnerDecider.ClaimRoot(owner, "abc123", verified: true, Now);

        claimed.Id.Should().Be(owner.Id);
        claimed.RootFingerprint.Should().Be("abc123");
        claimed.Verified.Should().BeTrue();
        claimed.ClaimedAt.Should().Be(Now);
    }

    [Fact]
    public void ClaimRoot_refuses_a_blank_fingerprint()
    {
        OwnerAggregate owner = Registered();

        Action act = () => OwnerDecider.ClaimRoot(owner, "  ", verified: false, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void OwnerAggregate_applies_the_claim_onto_its_root_fields()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ClaimRoot(owner, "fingerprint-1", verified: true, Now));

        owner.RootFingerprint.Should().Be("fingerprint-1");
        owner.RootFingerprintVerified.Should().BeTrue();
        owner.RootClaimedAt.Should().Be(Now);
    }

    [Fact]
    public void A_later_claim_replaces_the_earlier_one_rather_than_merging_with_it()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ClaimRoot(owner, "self-root", verified: true, Now));
        owner.Apply(OwnerDecider.ClaimRoot(owner, "real-root", verified: false, Now.AddMinutes(5)));

        owner.RootFingerprint.Should().Be("real-root");
        owner.RootFingerprintVerified.Should().BeFalse("the second join claimed someone else's root, unverified");
    }

    [Fact]
    public void A_named_voice_skill_round_trips_through_the_event_onto_the_aggregate()
    {
        OwnerAggregate owner = Registered();

        OwnerSettingsChanged changed = OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            Optional<VoiceSkillName>.Of(VoiceSkillName.Parse("my-voice")));
        owner.Apply(changed);

        changed.VoiceSkill.HasValue.Should().BeTrue();
        changed.VoiceSkill.Value!.Value.Should().Be("my-voice");
        owner.VoiceSkill.Value.Should().Be("my-voice");
    }

    [Fact]
    public void An_owner_names_no_voice_skill_until_they_say_so() =>
        Registered().VoiceSkill.Should().Be(VoiceSkillName.None);

    /// <summary>
    /// None is a legal explicit value, exactly as Unknown is for the re-request policy: it is what
    /// <c>--clear-voice-skill</c> records, and it has to be distinguishable from "not mentioned".
    /// </summary>
    [Fact]
    public void Clearing_the_preference_is_an_explicit_value_rather_than_an_omission()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            Optional<VoiceSkillName>.Of(VoiceSkillName.Parse("my-voice"))));

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1),
            Optional<VoiceSkillName>.Of(VoiceSkillName.None)));

        owner.VoiceSkill.Should().Be(VoiceSkillName.None);
    }

    [Fact]
    public void A_settings_change_that_never_mentions_the_voice_skill_leaves_it_alone()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            Optional<VoiceSkillName>.Of(VoiceSkillName.Parse("my-voice"))));

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now.AddMinutes(1)));

        owner.VoiceSkill.Value.Should().Be("my-voice");
        owner.ReviewRerequest.Should().Be(ReviewRerequestPolicy.Enabled);
    }

    /// <summary>
    /// The read side every prompt builder's caller actually reads, kept in step with the aggregate
    /// above — <c>h9k owner show</c> and <c>RunLauncher</c> both go through this projection.
    /// </summary>
    [Fact]
    public void The_projection_carries_the_preference_the_same_way_the_aggregate_does()
    {
        Guid id = DomainId.New();
        OwnerDetailsProjection projection = new();
        OwnerDetails view = projection.Create(new FakeEvent<OwnerRegistered>(
            new OwnerRegistered(id, "Test Owner", "owner@test.local", Now)));

        view.VoiceSkill.Should().Be(VoiceSkillName.None);

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now,
                Optional<VoiceSkillName>.Of(VoiceSkillName.Parse("my-voice")))),
            view);
        view.VoiceSkill.Value.Should().Be("my-voice");

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now.AddMinutes(1))),
            view);
        view.VoiceSkill.Value.Should().Be("my-voice", "a change that never mentions it leaves it alone");

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(2),
                Optional<VoiceSkillName>.Of(VoiceSkillName.None))),
            view);
        view.VoiceSkill.Should().Be(VoiceSkillName.None, "--clear-voice-skill forgets it");
    }

    private static OwnerAggregate Registered()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Test Owner", "owner@test.local", Now));
        return owner;
    }
}
