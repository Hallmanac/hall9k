using FluentAssertions;
using Hall9k.Domain.Features.Invite;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The invite-secret shape itself (idea 202383dc, T2; shortened and re-derived per the
/// 2026-09-26/27 security review, idea 6be68ee2): generation, both shapes' own parse-back into the
/// invite id (and, for the legacy shape only, the minting root), hashing, and the HMAC proof — all
/// pure, database- and ledger-free.
/// </summary>
public sealed class InviteSecretTests
{
    private static readonly string LegacyRoot = new('a', 64);

    /// <summary>A legacy 162-character secret, built by hand exactly the way the pre-shortening
    /// <c>InviteSecret.Generate(root, id)</c> used to: root, invite id (Guid "N" format), and a
    /// 64-hex-character random tail, dot-joined.</summary>
    private static string LegacySecret(string root, Guid inviteId) => $"{root}.{inviteId:N}.{new string('f', 64)}";

    [Fact]
    public void Generate_emits_the_pinned_new_shape()
    {
        string secret = InviteSecret.Generate();

        secret.Should().HaveLength(30, "a 4-character tag plus 26 base32 characters of a 16-byte tail is 30 characters");
        secret.Should().StartWith("invt");
        secret[4..].Should().MatchRegex("^[a-z2-7]{26}$", "the tail is lowercase RFC 4648 base32 with no padding");
    }

    [Fact]
    public void Two_generated_secrets_never_collide()
    {
        string first = InviteSecret.Generate();
        string second = InviteSecret.Generate();

        first.Should().NotBe(second, "the random tail alone is what makes each invite's secret unguessable");
    }

    [Fact]
    public void A_new_shape_secret_parses_with_a_derived_id_and_no_legacy_root()
    {
        string secret = InviteSecret.Generate();

        InviteSecret.TryParse(secret, out Guid inviteId, out string? legacyRoot).Should().BeTrue();
        inviteId.Should().Be(InviteSecret.DeriveId(secret));
        legacyRoot.Should().BeNull("a new-shape secret carries no minting root of its own");
    }

    [Fact]
    public void DeriveId_is_stable_for_the_same_secret_and_differs_across_secrets()
    {
        string secret = InviteSecret.Generate();

        InviteSecret.DeriveId(secret).Should().Be(InviteSecret.DeriveId(secret), "the same secret always derives the same id");
        InviteSecret.DeriveId(secret).Should().NotBe(
            InviteSecret.DeriveId(InviteSecret.Generate()), "two distinct secrets derive distinct ids");
    }

    [Fact]
    public void A_legacy_shape_secret_still_parses_with_its_own_embedded_root_and_id()
    {
        Guid inviteId = Guid.NewGuid();
        string secret = LegacySecret(LegacyRoot, inviteId);

        InviteSecret.TryParse(secret, out Guid parsedInviteId, out string? legacyRoot).Should().BeTrue();
        parsedInviteId.Should().Be(inviteId);
        legacyRoot.Should().Be(LegacyRoot, "the legacy 162-character shape carries the minting root in cleartext");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-secret-at-all")]
    [InlineData("too.few")]
    [InlineData("not-a-fingerprint.00000000000000000000000000000000.deadbeef")]
    [InlineData("invt")]
    [InlineData("invtnotenoughbase32chars")]
    [InlineData("badtag0123456789abcdefghijklmn")]
    public void TryParse_refuses_anything_not_shaped_like_a_real_secret(string? malformed)
    {
        InviteSecret.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Hash_is_deterministic_and_never_reveals_the_secret_itself()
    {
        string secret = InviteSecret.Generate();
        string hash = InviteSecret.Hash(secret);

        InviteSecret.Hash(secret).Should().Be(hash, "the same secret always hashes to the same value");
        hash.Should().NotBe(secret);
    }

    [Fact]
    public void ComputeProof_is_deterministic_and_bound_to_the_exact_fingerprint()
    {
        string secret = InviteSecret.Generate();
        string fingerprintA = new('b', 64);
        string fingerprintB = new('c', 64);

        string proofA = InviteSecret.ComputeProof(secret, fingerprintA);
        InviteSecret.ComputeProof(secret, fingerprintA).Should().Be(proofA, "the identical inputs always produce the identical proof");
        InviteSecret.ComputeProof(secret, fingerprintB).Should().NotBe(proofA, "a proof is bound to one specific node's own key, not merely to the secret");
    }

    [Fact]
    public void A_wrong_secret_never_produces_the_real_proof()
    {
        string realSecret = InviteSecret.Generate();
        string wrongSecret = InviteSecret.Generate();
        string fingerprint = new('d', 64);

        InviteSecret.ComputeProof(wrongSecret, fingerprint).Should().NotBe(InviteSecret.ComputeProof(realSecret, fingerprint));
    }
}
