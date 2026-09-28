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
    public void VerifyRoot_produces_an_event_that_flips_the_aggregate_verified_without_touching_the_fingerprint_or_claimed_at()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ClaimRoot(owner, "claimed-root", verified: false, Now));

        OwnerRootVerified verified = OwnerDecider.VerifyRoot(owner, Now.AddMinutes(10));
        owner.Apply(verified);

        verified.Id.Should().Be(owner.Id);
        verified.RootFingerprint.Should().Be("claimed-root");
        verified.VerifiedAt.Should().Be(Now.AddMinutes(10));
        owner.RootFingerprint.Should().Be("claimed-root", "reconciliation never changes which root was claimed");
        owner.RootFingerprintVerified.Should().BeTrue();
        owner.RootClaimedAt.Should().Be(Now, "reconciliation is not a re-claim — the original claim time is untouched");
    }

    [Fact]
    public void VerifyRoot_refuses_an_owner_with_no_root_claimed_yet()
    {
        OwnerAggregate owner = Registered();

        Action act = () => OwnerDecider.VerifyRoot(owner, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_verification_computed_against_a_root_that_is_no_longer_the_current_claim_is_ignored()
    {
        // The race independent pre-PR review (adversarial lens, medium) found: a stale in-memory
        // aggregate or a racing concurrent append can each produce a VerifyRoot event for a root
        // the owner has already moved on from by the time it is applied. Apply must never let that
        // stamp verified: true onto whatever root happens to be current now.
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ClaimRoot(owner, "old-root", verified: false, Now));
        OwnerRootVerified staleVerification = OwnerDecider.VerifyRoot(owner, Now.AddMinutes(5));

        owner.Apply(OwnerDecider.ClaimRoot(owner, "new-root", verified: false, Now.AddMinutes(10)));
        owner.Apply(staleVerification);

        owner.RootFingerprint.Should().Be("new-root");
        owner.RootFingerprintVerified.Should().BeFalse("the stale event verified old-root, not the owner's current claim");
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

    [Fact]
    public void An_owner_has_no_effective_display_name_for_any_project_until_they_set_one() =>
        Registered().EffectiveDisplayName(Guid.NewGuid()).Should().Be(DisplayName.None);

    [Fact]
    public void A_default_display_name_applies_to_every_project_with_no_entry_of_its_own()
    {
        OwnerAggregate owner = Registered();
        Guid projectId = Guid.NewGuid();

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            defaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse("Ada Lovelace"))));

        owner.EffectiveDisplayName(projectId).Value.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void A_projects_own_entry_takes_precedence_over_the_default()
    {
        OwnerAggregate owner = Registered();
        Guid projectId = Guid.NewGuid();
        Guid otherProjectId = Guid.NewGuid();

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            defaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse("Default Name"))));
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1),
            projectDisplayName: Optional<OwnerProjectDisplayName>.Of(
                new OwnerProjectDisplayName(projectId, DisplayName.Parse("Project Name")))));

        owner.EffectiveDisplayName(projectId).Value.Should().Be("Project Name");
        owner.EffectiveDisplayName(otherProjectId).Value.Should().Be("Default Name", "no entry of its own, so the default applies");
    }

    [Fact]
    public void Clearing_a_projects_own_entry_falls_back_to_the_default()
    {
        OwnerAggregate owner = Registered();
        Guid projectId = Guid.NewGuid();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            defaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse("Default Name"))));
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1),
            projectDisplayName: Optional<OwnerProjectDisplayName>.Of(
                new OwnerProjectDisplayName(projectId, DisplayName.Parse("Project Name")))));

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(2),
            projectDisplayName: Optional<OwnerProjectDisplayName>.Of(new OwnerProjectDisplayName(projectId, DisplayName.None))));

        owner.EffectiveDisplayName(projectId).Value.Should().Be("Default Name");
    }

    [Fact]
    public void Clearing_the_default_clears_it_for_every_project_with_no_entry_of_its_own()
    {
        OwnerAggregate owner = Registered();
        Guid projectId = Guid.NewGuid();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            defaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse("Default Name"))));

        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1),
            defaultDisplayName: Optional<DisplayName>.Of(DisplayName.None)));

        owner.EffectiveDisplayName(projectId).Should().Be(DisplayName.None);
    }

    [Fact]
    public void A_settings_change_that_never_mentions_the_display_name_leaves_it_alone()
    {
        OwnerAggregate owner = Registered();
        owner.Apply(OwnerDecider.ChangeSettings(
            owner, Optional<ReviewRerequestPolicy>.None, Now,
            defaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse("Ada Lovelace"))));

        owner.Apply(OwnerDecider.ChangeSettings(owner, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now.AddMinutes(1)));

        owner.EffectiveDisplayName(Guid.NewGuid()).Value.Should().Be("Ada Lovelace");
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

    [Fact]
    public void The_projection_resolves_the_effective_display_name_the_same_way_the_aggregate_does()
    {
        Guid id = DomainId.New();
        Guid projectId = DomainId.New();
        OwnerDetailsProjection projection = new();
        OwnerDetails view = projection.Create(new FakeEvent<OwnerRegistered>(
            new OwnerRegistered(id, "Test Owner", "owner@test.local", Now)));

        view.EffectiveDisplayName(projectId).Should().Be(DisplayName.None);

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now,
                DefaultDisplayName: Optional<DisplayName>.Of(DisplayName.Parse("Default Name")))),
            view);
        view.EffectiveDisplayName(projectId).Value.Should().Be("Default Name");

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(1),
                ProjectDisplayName: Optional<OwnerProjectDisplayName>.Of(
                    new OwnerProjectDisplayName(projectId, DisplayName.Parse("Project Name"))))),
            view);
        view.EffectiveDisplayName(projectId).Value.Should().Be("Project Name", "the project's own entry outranks the default");

        projection.Apply(
            new FakeEvent<OwnerSettingsChanged>(new OwnerSettingsChanged(
                id, Optional<ReviewRerequestPolicy>.None, Now.AddMinutes(2),
                ProjectDisplayName: Optional<OwnerProjectDisplayName>.Of(new OwnerProjectDisplayName(projectId, DisplayName.None)))),
            view);
        view.EffectiveDisplayName(projectId).Value.Should().Be("Default Name", "clearing the project's own entry falls back to the default");
    }

    [Fact]
    public void The_projection_flips_verified_the_same_way_the_aggregate_does()
    {
        Guid id = DomainId.New();
        OwnerDetailsProjection projection = new();
        OwnerDetails view = projection.Create(new FakeEvent<OwnerRegistered>(
            new OwnerRegistered(id, "Test Owner", "owner@test.local", Now)));

        projection.Apply(new FakeEvent<OwnerRootClaimed>(new OwnerRootClaimed(id, "claimed-root", false, Now)), view);
        view.RootFingerprintVerified.Should().BeFalse();

        projection.Apply(new FakeEvent<OwnerRootVerified>(new OwnerRootVerified(id, "claimed-root", Now.AddMinutes(10))), view);
        view.RootFingerprintVerified.Should().BeTrue();
        view.RootFingerprint.Should().Be("claimed-root", "reconciliation never changes which root was claimed");
    }

    [Fact]
    public void The_projection_ignores_a_verification_for_a_root_that_is_no_longer_current()
    {
        Guid id = DomainId.New();
        OwnerDetailsProjection projection = new();
        OwnerDetails view = projection.Create(new FakeEvent<OwnerRegistered>(
            new OwnerRegistered(id, "Test Owner", "owner@test.local", Now)));

        projection.Apply(new FakeEvent<OwnerRootClaimed>(new OwnerRootClaimed(id, "new-root", false, Now)), view);
        projection.Apply(new FakeEvent<OwnerRootVerified>(new OwnerRootVerified(id, "old-root", Now.AddMinutes(10))), view);

        view.RootFingerprintVerified.Should().BeFalse("the event verified old-root, not the view's current claim");
    }

    [Fact]
    public void VouchNode_produces_an_event_carrying_the_node_and_its_fingerprint()
    {
        OwnerAggregate owner = Registered();
        Guid nodeId = Guid.NewGuid();

        NodeVouched vouched = OwnerDecider.VouchNode(owner, nodeId, "node-fingerprint", Now);

        vouched.OwnerId.Should().Be(owner.Id);
        vouched.NodeId.Should().Be(nodeId);
        vouched.NodeFingerprint.Should().Be("node-fingerprint");
        vouched.IssuedAt.Should().Be(Now);
    }

    [Fact]
    public void VouchNode_refuses_an_empty_node_id()
    {
        OwnerAggregate owner = Registered();

        Action act = () => OwnerDecider.VouchNode(owner, Guid.Empty, "node-fingerprint", Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void VouchNode_refuses_a_blank_fingerprint()
    {
        OwnerAggregate owner = Registered();

        Action act = () => OwnerDecider.VouchNode(owner, Guid.NewGuid(), "  ", Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void RevokeNode_refuses_an_empty_node_id()
    {
        OwnerAggregate owner = Registered();

        Action act = () => OwnerDecider.RevokeNode(owner, Guid.Empty, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void OwnerAggregate_tracks_a_vouched_node_and_forgets_it_once_revoked()
    {
        OwnerAggregate owner = Registered();
        Guid nodeId = Guid.NewGuid();

        owner.Apply(OwnerDecider.VouchNode(owner, nodeId, "node-fingerprint", Now));
        owner.VouchedNodes.Should().ContainKey(nodeId);

        owner.Apply(OwnerDecider.RevokeNode(owner, nodeId, Now.AddMinutes(5)));
        owner.VouchedNodes.Should().NotContainKey(nodeId);
    }

    private static OwnerAggregate Registered()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Test Owner", "owner@test.local", Now));
        return owner;
    }
}
