using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Daemon.Invites;
using Hall9k.Domain.Features.Invite;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The invite sweep's own candidate resolution and proof comparison (2026-09-26/27 security review,
/// idea 6be68ee2) — pure, database- and git-free: <see cref="InviteSweepEngine.TryResolveCandidateAsync"/>
/// and <see cref="InviteSweepEngine.ProofMatches"/> are both <c>IDocumentStore</c>-free statics
/// exactly so they can be driven directly against <see cref="FakeLedgerCommitReader"/>, the same
/// seam <c>ProjectJoinCommandTests</c>' own carry-path tests already use (Brian's 2026-09-13 testing
/// rule).
/// </summary>
public sealed class InviteSweepEngineTests
{
    private const string RepositoryPath = "repo-under-test";
    private const string RefName = "refs/hall9k/ledger/nodes/11111111-1111-1111-1111-111111111111";
    private const string Path = "nodes/11111111-1111-1111-1111-111111111111/node.yaml";
    private static readonly Guid CandidateNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string VictimPublicKeyLine = "ssh-ed25519 dmljdGltLWtleS1tYXRlcmlhbC0zMi1ieXRlcyEhIQ== victim";
    private const string AttackerPublicKeyLine = "ssh-ed25519 YXR0YWNrZXIta2V5LW1hdGVyaWFsLTMyLWJ5dGVzIQ== attacker";
    private const string OwnerFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task A_candidate_signed_by_its_own_public_key_is_resolved()
    {
        string secret = InviteSecret.Generate();
        string victimFingerprint = NodeKeyStore.Fingerprint(VictimPublicKeyLine);
        string proof = InviteSecret.ComputeProof(secret, victimFingerprint);
        string content = NodeFileYaml(VictimPublicKeyLine, OwnerFingerprint, proof);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, LedgerSignedCommit> { [Path] = new(content, "sha", "raw-commit-signed-by-victim") },
            signed: true);

        InviteSweepEngine.CandidateNode? candidate = await InviteSweepEngine.TryResolveCandidateAsync(
            commitReader, RepositoryPath, RefName, Path, CandidateNodeId, NullLogger.Instance, CancellationToken.None);

        candidate.Should().NotBeNull("the commit that produced this node.yaml is genuinely signed by the public_key it carries");
        candidate!.KeyFingerprint.Should().Be(victimFingerprint);
        InviteSweepEngine.ProofMatches(candidate, secret).Should().BeTrue();
    }

    /// <summary>
    /// The attack criterion 3 exists to close: a repository collaborator with no membership of their
    /// own copies a real invitee's own genuine proof — the HMAC only the secret and the victim's own
    /// key fingerprint could ever produce — into a node file naming the VICTIM's own public_key (so
    /// the copied proof's fingerprint binding still matches), but the commit that actually carries
    /// that file is signed by the attacker's own key, since only the victim's own private key could
    /// ever sign a commit that verifies against the victim's own public_key line. The signature gate
    /// must refuse this candidate outright — never merely fail to vouch it later — so it never even
    /// reaches the proof comparison.
    /// </summary>
    [Fact]
    public async Task A_copied_proof_in_the_attackers_own_signed_file_is_not_resolved_as_a_candidate()
    {
        string secret = InviteSecret.Generate();
        string victimFingerprint = NodeKeyStore.Fingerprint(VictimPublicKeyLine);
        string copiedProof = InviteSecret.ComputeProof(secret, victimFingerprint);

        // The attacker's own node file: claims the VICTIM's own public_key (to make the copied proof's
        // fingerprint binding match), but the commit producing it is only ever signed by the
        // attacker's own key — never the victim's, which the attacker does not hold.
        string content = NodeFileYaml(VictimPublicKeyLine, OwnerFingerprint, copiedProof);

        FakeLedgerCommitReader commitReader = new(
            new Dictionary<string, LedgerSignedCommit> { [Path] = new(content, "sha", "raw-commit-signed-by-attacker") },
            isSignedBy: (_, publicKeyLine) => publicKeyLine == AttackerPublicKeyLine);

        InviteSweepEngine.CandidateNode? candidate = await InviteSweepEngine.TryResolveCandidateAsync(
            commitReader, RepositoryPath, RefName, Path, CandidateNodeId, NullLogger.Instance, CancellationToken.None);

        candidate.Should().BeNull(
            "the commit is not signed by the public_key the file itself carries (the victim's), so it is never a candidate at all");
    }

    [Fact]
    public void ProofMatches_is_true_only_for_the_real_proof()
    {
        string secret = InviteSecret.Generate();
        string fingerprint = new('e', 64);
        string realProof = InviteSecret.ComputeProof(secret, fingerprint);

        InviteSweepEngine.CandidateNode realCandidate = new(CandidateNodeId, fingerprint, OwnerFingerprint, VictimPublicKeyLine, realProof);
        InviteSweepEngine.CandidateNode wrongCandidate = realCandidate with { Proof = new string('0', realProof.Length) };
        InviteSweepEngine.CandidateNode shorterCandidate = realCandidate with { Proof = "short" };

        InviteSweepEngine.ProofMatches(realCandidate, secret).Should().BeTrue();
        InviteSweepEngine.ProofMatches(wrongCandidate, secret).Should().BeFalse("a same-length but wrong proof must never match");
        InviteSweepEngine.ProofMatches(shorterCandidate, secret).Should().BeFalse(
            "a differently-sized proof (constant-time comparison, CryptographicOperations.FixedTimeEquals) must never match either");
    }

    private static string NodeFileYaml(string publicKeyLine, string ownerFingerprint, string inviteProof) =>
        $"node_id: \"{CandidateNodeId}\"\n"
        + $"public_key: \"{publicKeyLine}\"\n"
        + $"owner_fingerprint: \"{ownerFingerprint}\"\n"
        + "machine_name: \"test-machine\"\n"
        + "operating_system: \"linux\"\n"
        + $"invite_proof: \"{inviteProof}\"\n";
}
