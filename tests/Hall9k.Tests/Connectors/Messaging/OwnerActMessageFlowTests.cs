using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors.Messaging;

/// <summary>
/// The owner-act request/outcome pair (idea 6be68ee2, companion 1bb803e1) carried end to end across
/// <see cref="InMemoryMessageTransport"/> — no Docker, no real git (Brian's 2026-09-13 testing rule;
/// the existing RequiresDocker InviteCommandsTests stay the only Docker cover for this feature). Each
/// test sends a real, encoded envelope through the fake transport, reads it back the way a receiving
/// node's own daemon would, and feeds the decoded request into <see cref="OwnerActRequestWatchLoop.Decide"/>
/// — the identical pure verdict the real watch loop reaches.
/// </summary>
public sealed class OwnerActMessageFlowTests
{
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string RootPublicKeyLine = "ssh-ed25519 AAAAFAKEROOT root";
    private const string RequesterPublicKeyLine = "ssh-ed25519 AAAAREQUESTERKEY requester";
    private const string CandidateOwnerFingerprint =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly string RootFingerprint = NodeKeyStore.Fingerprint(RootPublicKeyLine);
    private static readonly string RequesterFingerprint = NodeKeyStore.Fingerprint(RequesterPublicKeyLine);
    private static readonly Guid RootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RequesterNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InviteId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, string publicKeyLine, CancellationToken cancellationToken)
    {
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml",
                $"node_id: \"{nodeId}\"\npublic_key: \"{publicKeyLine}\"\n",
                ExpectedBlobId: null, "seed node file", new LedgerCommitter("seed", "seed@hall9k.local"),
                new LedgerSigningKey("/dev/null/seed")),
            cancellationToken);
    }

    /// <summary>The requester's own owner is vouched into the root's own fleet — the everyday case
    /// (a non-root node of the SAME owner's fleet asking its own root to act).</summary>
    private static TrustChain BuildChainWithRequesterVouched()
    {
        TrustedNode requesterNode = new(
            RequesterNodeId.ToString(), RequesterPublicKeyLine, RequesterFingerprint, DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new(RootFingerprint, RootPublicKeyLine, [requesterNode], RootNodeId: RootNodeId.ToString());
        return new TrustChain(
            new Dictionary<string, TrustedOwner> { [RootFingerprint] = owner },
            [new ProjectMember(RootFingerprint, MembershipRole.Owner, DateTimeOffset.UnixEpoch)]);
    }

    /// <summary>Sends one owner-act request from the requester's own outbox and reads it back on the
    /// root's own side, returning the decoded request and the sender fingerprint the transport itself
    /// resolved — the identical two facts <c>OwnerActRequestWatchLoop.ReactToRequestAsync</c> reads
    /// before it ever calls <see cref="OwnerActRequestWatchLoop.Decide"/>.</summary>
    private static async Task<(OwnerActEnvelopeCodec.OwnerActRequestRecord Request, string? SenderFingerprint)> SendAndReadRequestAsync(
        FakeLedger ledger, TrustChain chain, ProjectMemberRole role, DateTimeOffset queuedAt, CancellationToken cancellationToken)
    {
        await SeedNodeFileAsync(ledger, RequesterNodeId, RequesterPublicKeyLine, cancellationToken);

        OwnerActEnvelopeCodec.OwnerActRequestRecord request = new(
            InviteId, RequesterNodeId, CandidateOwnerFingerprint, role, queuedAt);
        MessageEnvelopeV1 envelope = new(
            1, queuedAt, RequesterNodeId, RootFingerprint, MessageAudience.Node(RootNodeId), InviteId.ToString(),
            MessageKind.OwnerActRequest, OwnerActEnvelopeCodec.Encode(request));

        InMemoryMessageTransport transport = new(ledger, new FakeLedgerChainReader(chain));
        await transport.SendAsync(
            RepositoryPath, RequesterNodeId, envelope.Seq, MessageEnvelopeCodec.Encode(envelope),
            new LedgerCommitter("requester", "requester@hall9k.local"), new LedgerSigningKey("/dev/null/requester"),
            cancellationToken);

        TransportReadResult result = await transport.ReadSinceAsync(RepositoryPath, RequesterNodeId, sinceSeq: 0, cancellationToken, chain);
        result.SenderVouched.Should().BeTrue("the requester's own node file is seeded and readable");
        result.Envelopes.Should().ContainSingle();

        MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(result.Envelopes[0].Content);
        decoded.Outcome.Should().Be(MessageEnvelopeCodec.DecodeOutcome.Parsed);
        OwnerActEnvelopeCodec.OwnerActRequestRecord? decodedRequest = OwnerActEnvelopeCodec.TryDecodeRequest(decoded.Envelope!.Body);
        decodedRequest.Should().NotBeNull();

        return (decodedRequest!, result.SenderFingerprint);
    }

    [Fact]
    public async Task AMemberRoleWriteIsPerformedAutomatically()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        TrustChain chain = BuildChainWithRequesterVouched();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        (OwnerActEnvelopeCodec.OwnerActRequestRecord request, string? senderFingerprint) =
            await SendAndReadRequestAsync(ledger, chain, ProjectMemberRole.Member, now, cts.Token);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint, RootFingerprint, RequesterNodeId);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(RootFingerprint, RootFingerprint);
        OwnerActRequestVerdict verdict = OwnerActRequestWatchLoop.Decide(verified, expired: false, isLiveRootKey, request.Role);

        verdict.Should().Be(OwnerActRequestVerdict.PerformMemberWrite);

        LedgerCommitter committer = new("root", "root@hall9k.local");
        LedgerSigningKey signingKey = new("/dev/null/root");
        string? commitId = await MemberVouchLedgerWriter.WriteAsync(
            ledger, RepositoryPath, request.CandidateOwnerFingerprint, request.Role, request.IssuedAt, committer, signingKey, cts.Token);

        commitId.Should().NotBeNull("this is the first time this exact content was written");
        LedgerFile written = await ledger.ReadAsync(
            RepositoryPath, "refs/hall9k/ledger/members", $"members/{CandidateOwnerFingerprint}.yaml", cts.Token);
        written.Content.Should().Contain($"root_fingerprint: \"{CandidateOwnerFingerprint}\"").And.Contain("role: \"member\"");
    }

    [Fact]
    public async Task AnOwnerRoleWriteIsHeldRatherThanWritten()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        TrustChain chain = BuildChainWithRequesterVouched();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        (OwnerActEnvelopeCodec.OwnerActRequestRecord request, string? senderFingerprint) =
            await SendAndReadRequestAsync(ledger, chain, ProjectMemberRole.Owner, now, cts.Token);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint, RootFingerprint, RequesterNodeId);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(RootFingerprint, RootFingerprint);
        OwnerActRequestVerdict verdict = OwnerActRequestWatchLoop.Decide(verified, expired: false, isLiveRootKey, request.Role);

        verdict.Should().Be(OwnerActRequestVerdict.Hold);
        ledger.Writes.Should().NotContain(
            write => write.Path.StartsWith("members/", StringComparison.Ordinal),
            "an owner-role write is never made automatically, only held for the root's own human");
    }

    /// <summary>
    /// The security gate this task exists to prove: a node belonging to a DIFFERENT owner's own
    /// fleet (never vouched into the root's own chain at all) sends the identical shape of request —
    /// refused before anything about role or expiry is even asked, so a member's own node can never
    /// request on this owner's behalf.
    /// </summary>
    [Fact]
    public async Task AMembersNodeNotVouchedUnderThisOwnersOwnChainIsRefused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        // The requester's own node is genuinely vouched — but into a DIFFERENT owner's own chain, a
        // legitimate fellow project member the transport itself is happy to read from
        // (SenderVouched stays true — that check alone is not the gate this task closes). The root's
        // own chain (RootFingerprint) never names this node at all, the shape of a member's node
        // trying to request on an owner it does not belong to's behalf.
        const string OtherOwnerFingerprint = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        TrustedNode requesterNode = new(
            RequesterNodeId.ToString(), RequesterPublicKeyLine, RequesterFingerprint, DateTimeOffset.UnixEpoch);
        TrustedOwner root = new(RootFingerprint, RootPublicKeyLine, [], RootNodeId: RootNodeId.ToString());
        TrustedOwner otherOwner = new(OtherOwnerFingerprint, "ssh-ed25519 AAAAOTHEROWNERKY otherowner", [requesterNode]);
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { [RootFingerprint] = root, [OtherOwnerFingerprint] = otherOwner },
            [
                new ProjectMember(RootFingerprint, MembershipRole.Owner, DateTimeOffset.UnixEpoch),
                new ProjectMember(OtherOwnerFingerprint, MembershipRole.Member, DateTimeOffset.UnixEpoch),
            ]);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        (OwnerActEnvelopeCodec.OwnerActRequestRecord request, string? senderFingerprint) =
            await SendAndReadRequestAsync(ledger, chain, ProjectMemberRole.Member, now, cts.Token);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint, RootFingerprint, RequesterNodeId);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(RootFingerprint, RootFingerprint);
        OwnerActRequestVerdict verdict = OwnerActRequestWatchLoop.Decide(verified, expired: false, isLiveRootKey, request.Role);

        verified.Should().BeFalse();
        verdict.Should().Be(OwnerActRequestVerdict.RefuseNotVerified);
    }

    [Fact]
    public async Task AResentRequestReproducesByteIdenticalContentAndMakesNoSecondCommit()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
        LedgerCommitter committer = new("root", "root@hall9k.local");
        LedgerSigningKey signingKey = new("/dev/null/root");

        string? firstCommitId = await MemberVouchLedgerWriter.WriteAsync(
            ledger, RepositoryPath, CandidateOwnerFingerprint, ProjectMemberRole.Member, issuedAt, committer, signingKey, cts.Token);
        // The identical request, carrying the identical issued_at (the sweep's adopt-issued_at
        // pattern) — a genuine resend, not a fresh write of different content.
        string? secondCommitId = await MemberVouchLedgerWriter.WriteAsync(
            ledger, RepositoryPath, CandidateOwnerFingerprint, ProjectMemberRole.Member, issuedAt, committer, signingKey, cts.Token);

        firstCommitId.Should().NotBeNull();
        secondCommitId.Should().BeNull("the content already matches, so the resend makes no new commit at all");
        ledger.Writes.Should().ContainSingle("only the first write ever actually reached ILedger.WriteAsync");
    }

    [Fact]
    public async Task ARequestForAnInviteThatHasExpiredIsAnsweredExpired()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        TrustChain chain = BuildChainWithRequesterVouched();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset queuedAt = now - InviteLifetime - TimeSpan.FromMinutes(1);

        (OwnerActEnvelopeCodec.OwnerActRequestRecord request, string? senderFingerprint) =
            await SendAndReadRequestAsync(ledger, chain, ProjectMemberRole.Member, queuedAt, cts.Token);

        InviteLedgerRecord inviteRecord = InviteRecordFor(queuedAt, request.Role);
        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint, RootFingerprint, RequesterNodeId);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(RootFingerprint, RootFingerprint);
        bool expired = OwnerActRequestWatchLoop.IsInviteExpired(inviteRecord, now);
        OwnerActRequestVerdict verdict = OwnerActRequestWatchLoop.Decide(verified, expired, isLiveRootKey, request.Role);

        expired.Should().BeTrue();
        verdict.Should().Be(OwnerActRequestVerdict.Expired);
        ledger.Writes.Should().NotContain(
            write => write.Path.StartsWith("members/", StringComparison.Ordinal), "an expired request is answered expired, never acted on");
    }

    /// <summary>
    /// The reported failure: the root answered a request Expired once it was more than ten minutes
    /// old, though the invite had days left, and nothing ever asked again. A request the root reads
    /// hours after it was sent now runs the whole path: verdict, member write, and the minting
    /// node's own spend.
    /// </summary>
    [Fact]
    public async Task AMemberRoleRequestReachingTheRootHoursLateStillEndsWithTheWriteLandedAndTheInviteSpent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        TrustChain chain = BuildChainWithRequesterVouched();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset queuedAt = now - TimeSpan.FromHours(11);

        (OwnerActEnvelopeCodec.OwnerActRequestRecord request, string? senderFingerprint) =
            await SendAndReadRequestAsync(ledger, chain, ProjectMemberRole.Member, queuedAt, cts.Token);

        InviteLedgerRecord inviteRecord = InviteRecordFor(queuedAt, request.Role);
        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint, RootFingerprint, RequesterNodeId);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(RootFingerprint, RootFingerprint);
        bool expired = OwnerActRequestWatchLoop.IsInviteExpired(inviteRecord, now);
        OwnerActRequestVerdict verdict = OwnerActRequestWatchLoop.Decide(verified, expired, isLiveRootKey, request.Role);

        verdict.Should().Be(OwnerActRequestVerdict.PerformMemberWrite);
        OwnerActRequestWatchLoop.ValidateInviteAgainstLedger(inviteRecord, request.Role, existingMemberRole: null, now)
            .Should().BeNull("the invite is real, unspent and unexpired");

        await WriteMemberAsync(ledger, request, cts.Token);
        InviteAggregate invite = await SpendOnMintingNodeAsync(ledger, request, queuedAt, now, cts.Token);

        invite.Spent.Should().BeTrue();
    }

    [Fact]
    public async Task AnOwnerRoleRequestReachingTheRootHoursLateIsHeldAndApprovedHoursLaterThenSpent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        FakeLedger ledger = new();
        TrustChain chain = BuildChainWithRequesterVouched();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset queuedAt = now - TimeSpan.FromHours(11);

        (OwnerActEnvelopeCodec.OwnerActRequestRecord request, string? senderFingerprint) =
            await SendAndReadRequestAsync(ledger, chain, ProjectMemberRole.Owner, queuedAt, cts.Token);

        InviteLedgerRecord inviteRecord = InviteRecordFor(queuedAt, request.Role);
        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint, RootFingerprint, RequesterNodeId);
        bool isLiveRootKey = chain.IsLiveRootKeyOfOwner(RootFingerprint, RootFingerprint);
        bool expired = OwnerActRequestWatchLoop.IsInviteExpired(inviteRecord, now);
        OwnerActRequestVerdict verdict = OwnerActRequestWatchLoop.Decide(verified, expired, isLiveRootKey, request.Role);

        verdict.Should().Be(OwnerActRequestVerdict.Hold);

        // The hold is placed now and approved eleven hours after that, well past the old ten-minute wait.
        OwnerActHeld held = OwnerActHoldDecider.Hold(
            Guid.NewGuid(), Guid.NewGuid(), RepositoryPath, request.InviteId, request.RequesterNodeId,
            request.CandidateOwnerFingerprint, request.Role, request.IssuedAt, now, inviteRecord.ExpiresAt);
        OwnerActHoldAggregate hold = new();
        hold.Apply(held);
        DateTimeOffset approvedAt = now + TimeSpan.FromHours(11);
        OwnerActHoldDetails details = new() { HeldAt = held.HeldAt, InviteExpiresAt = held.InviteExpiresAt };
        OwnerActRequestWatchLoop.IsHoldStale(details, approvedAt).Should().BeFalse("the invite is still live eleven hours on");

        hold.Apply(OwnerActHoldDecider.Approve(hold, commitId: null, approvedAt));
        hold.Approved.Should().BeTrue();

        await WriteMemberAsync(ledger, request, cts.Token);
        InviteAggregate invite = await SpendOnMintingNodeAsync(ledger, request, queuedAt, approvedAt, cts.Token);

        invite.Spent.Should().BeTrue();
    }

    [Fact]
    public void AnOwnerRoleHoldExpiresWithTheInviteAndNotBefore()
    {
        DateTimeOffset heldAt = DateTimeOffset.UtcNow;
        OwnerActHoldDetails hold = new() { HeldAt = heldAt, InviteExpiresAt = heldAt + TimeSpan.FromHours(72) };

        OwnerActRequestWatchLoop.IsHoldStale(hold, heldAt + TimeSpan.FromHours(71)).Should().BeFalse();
        OwnerActRequestWatchLoop.IsHoldStale(hold, heldAt + TimeSpan.FromHours(72)).Should().BeTrue();
        hold.WaitEndsAt.Should().Be(heldAt + TimeSpan.FromHours(72));
    }

    [Fact]
    public void AHoldRecordedBeforeTheInviteExpiryWasCarriedKeepsItsTenMinuteWait()
    {
        DateTimeOffset heldAt = DateTimeOffset.UtcNow;
        OwnerActHoldDetails hold = new() { HeldAt = heldAt };

        hold.WaitEndsAt.Should().Be(heldAt + TimeSpan.FromMinutes(10));
        OwnerActRequestWatchLoop.IsHoldStale(hold, heldAt + TimeSpan.FromMinutes(9)).Should().BeFalse();
        OwnerActRequestWatchLoop.IsHoldStale(hold, heldAt + TimeSpan.FromMinutes(11)).Should().BeTrue();
    }

    private static readonly TimeSpan InviteLifetime = TimeSpan.FromHours(72);

    private static InviteLedgerRecord InviteRecordFor(DateTimeOffset mintedAt, ProjectMemberRole role) =>
        new("secret-hash", InviteClaimKind.MemberOfProject, role, mintedAt + InviteLifetime, Spent: false);

    /// <summary>The root's member write, exactly as both the automatic and the approved path make it.</summary>
    private static async Task WriteMemberAsync(
        FakeLedger ledger, OwnerActEnvelopeCodec.OwnerActRequestRecord request, CancellationToken cancellationToken)
    {
        string? commitId = await MemberVouchLedgerWriter.WriteAsync(
            ledger, RepositoryPath, request.CandidateOwnerFingerprint, request.Role, request.IssuedAt,
            new LedgerCommitter("root", "root@hall9k.local"), new LedgerSigningKey("/dev/null/root"), cancellationToken);

        commitId.Should().NotBeNull();
        LedgerFile written = await ledger.ReadAsync(
            RepositoryPath, "refs/hall9k/ledger/members", $"members/{request.CandidateOwnerFingerprint}.yaml", cancellationToken);
        written.Content.Should().Contain($"role: \"{request.Role.Value}\"");
    }

    /// <summary>What the minting node does once the root's outcome names the write done: the spend in
    /// the ledger, then the invite's own spend.</summary>
    private static async Task<InviteAggregate> SpendOnMintingNodeAsync(
        FakeLedger ledger, OwnerActEnvelopeCodec.OwnerActRequestRecord request, DateTimeOffset mintedAt, DateTimeOffset spentAt,
        CancellationToken cancellationToken)
    {
        InviteAggregate invite = new();
        invite.Apply(InviteDecider.Mint(
            request.InviteId, RequesterNodeId, RootFingerprint, InviteClaimKind.MemberOfProject, request.Role,
            Guid.NewGuid(), RepositoryPath, "secret", "secret-hash", mintedAt, mintedAt + InviteLifetime));

        await InviteSpendLedgerWriter.WriteAsync(
            ledger, RepositoryPath, RootFingerprint, invite.Id, invite.SecretHash, invite.Claim, invite.Role, invite.ExpiresAt,
            new LedgerCommitter("requester", "requester@hall9k.local"), new LedgerSigningKey("/dev/null/requester"),
            cancellationToken);
        LedgerFile spentRecord = await ledger.ReadAsync(
            RepositoryPath, InviteLedgerRecord.RefName(RootFingerprint), InviteLedgerRecord.PathFor(RootFingerprint, invite.Id),
            cancellationToken);
        InviteLedgerRecord.Parse(spentRecord.Content).Should().Match<InviteLedgerRecord>(record => record.Spent);

        invite.Apply(InviteDecider.Spend(invite, Guid.NewGuid(), request.CandidateOwnerFingerprint, spentAt));
        return invite;
    }
}
