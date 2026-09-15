using FluentAssertions;
using Hall9k.Domain.Features.Invite;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The invite-secret shape itself (idea 202383dc, T2): generation, parsing back into the minting
/// root and invite id, hashing, and the HMAC proof — all pure, database- and ledger-free.
/// </summary>
public sealed class InviteSecretTests
{
    private static readonly string Root = new('a', 64);

    [Fact]
    public void Generate_round_trips_through_TryParse()
    {
        Guid inviteId = Guid.NewGuid();
        string secret = InviteSecret.Generate(Root, inviteId);

        InviteSecret.TryParse(secret, out string parsedRoot, out Guid parsedInviteId).Should().BeTrue();
        parsedRoot.Should().Be(Root);
        parsedInviteId.Should().Be(inviteId);
    }

    [Fact]
    public void Two_generated_secrets_for_the_same_invite_never_collide()
    {
        Guid inviteId = Guid.NewGuid();
        string first = InviteSecret.Generate(Root, inviteId);
        string second = InviteSecret.Generate(Root, inviteId);

        first.Should().NotBe(second, "the random component alone is what makes each invite's secret unguessable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-secret-at-all")]
    [InlineData("too.few")]
    [InlineData("not-a-fingerprint.00000000000000000000000000000000.deadbeef")]
    public void TryParse_refuses_anything_not_shaped_like_a_real_secret(string? malformed)
    {
        InviteSecret.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Hash_is_deterministic_and_never_reveals_the_secret_itself()
    {
        string secret = InviteSecret.Generate(Root, Guid.NewGuid());
        string hash = InviteSecret.Hash(secret);

        InviteSecret.Hash(secret).Should().Be(hash, "the same secret always hashes to the same value");
        hash.Should().NotBe(secret);
    }

    [Fact]
    public void ComputeProof_is_deterministic_and_bound_to_the_exact_fingerprint()
    {
        string secret = InviteSecret.Generate(Root, Guid.NewGuid());
        string fingerprintA = new('b', 64);
        string fingerprintB = new('c', 64);

        string proofA = InviteSecret.ComputeProof(secret, fingerprintA);
        InviteSecret.ComputeProof(secret, fingerprintA).Should().Be(proofA, "the identical inputs always produce the identical proof");
        InviteSecret.ComputeProof(secret, fingerprintB).Should().NotBe(proofA, "a proof is bound to one specific node's own key, not merely to the secret");
    }

    [Fact]
    public void A_wrong_secret_never_produces_the_real_proof()
    {
        string realSecret = InviteSecret.Generate(Root, Guid.NewGuid());
        string wrongSecret = InviteSecret.Generate(Root, Guid.NewGuid());
        string fingerprint = new('d', 64);

        InviteSecret.ComputeProof(wrongSecret, fingerprint).Should().NotBe(InviteSecret.ComputeProof(realSecret, fingerprint));
    }
}
