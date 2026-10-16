using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.TestSupport;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="OwnerActAskLookup"/>: what <c>h9k status</c> lists as this node's own outstanding
/// owner-act asks. The case worth a test of its own is an invitee who has since joined through a
/// different invite, which must not leave the first invite's ask printing a re-mint line until that
/// invite expires on its own clock.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class OwnerActAskLookupTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string CandidateFingerprint = "candidate-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private readonly ScopedTestHome _scopedHome = new();

    public OwnerActAskLookupTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync()
    {
        _scopedHome.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task An_expired_answer_is_listed_until_the_candidate_has_joined_through_another_invite(
        bool candidateIsMember, bool expectedListed)
    {
        Guid myNodeId = DomainId.New();
        Guid rootNodeId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid inviteId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();

        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = "demo",
            RepositoryPath = "/does/not/matter",
            Members = candidateIsMember ? new() { [CandidateFingerprint] = ProjectMemberRole.Member } : [],
        });
        session.Events.StartStream(inviteId, new InviteMinted(
            inviteId, myNodeId, "my-fingerprint", InviteClaimKind.MemberOfProject, ProjectMemberRole.Member, projectId,
            "/does/not/matter", "secret", "secret-hash", Now, Now.AddHours(72)));

        OwnerActEnvelopeCodec.OwnerActRequestRecord request = new(
            inviteId, myNodeId, CandidateFingerprint, ProjectMemberRole.Member, Now);
        await MessageOutbox.QueueAsync(
            session, myNodeId, projectId, "my-fingerprint", MessageAudience.Node(rootNodeId), inviteId.ToString(),
            MessageKind.OwnerActRequest, OwnerActEnvelopeCodec.Encode(request), Now, CancellationToken.None);

        MessageEnvelopeV1 expiredEnvelope = new(
            1, Now.AddMinutes(11), rootNodeId, "root-fingerprint", MessageAudience.Node(myNodeId), inviteId.ToString(),
            MessageKind.OwnerActOutcome,
            OwnerActEnvelopeCodec.Encode(new OwnerActEnvelopeCodec.OwnerActOutcomeRecord(
                inviteId, OwnerActEnvelopeCodec.OwnerActVerdict.Expired, CommitId: null, Reason: null)));
        session.Events.StartStream<MessageAggregate>(
            MessageStreamId.ForMessage(rootNodeId, projectId, 1),
            MessageDecider.Receive(rootNodeId, projectId, expiredEnvelope, Now.AddMinutes(12)));
        await session.SaveChangesAsync(CancellationToken.None);

        IReadOnlyList<OwnerActAskLookup.PendingOwnerAct> asks =
            await OwnerActAskLookup.FindUnansweredAsync(session, myNodeId, Now.AddHours(1), CancellationToken.None);

        (asks.Count == 1).Should().Be(expectedListed);
        if (expectedListed)
        {
            asks.Single().Verdict.Should().Be(OwnerActEnvelopeCodec.OwnerActVerdict.Expired);
        }
    }
}
