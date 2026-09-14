using FluentAssertions;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
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

    private static OwnerAggregate Registered()
    {
        OwnerAggregate owner = new();
        owner.Apply(OwnerDecider.Register(DomainId.New(), "Test Owner", "owner@test.local", Now));
        return owner;
    }
}
