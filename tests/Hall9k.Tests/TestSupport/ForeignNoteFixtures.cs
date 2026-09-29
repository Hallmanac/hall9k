using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Replication;
using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// One fixed two-owner chain and the senders in it, shared by every test that proves a replicated
/// note is judged by its verified sender: the local owner has two nodes (a root node and a vouched
/// one), and a teammate owner has one node and a declared GitHub account. Fixed literals rather
/// than generated ids, since golden fixtures print them.
/// </summary>
internal static class ForeignNoteFixtures
{
    public const string LocalRoot = "SHA256:localownerrootfingerprint00000001";
    public const string TeammateRoot = "SHA256:teammateownerrootfingerprint0002";

    public static readonly Guid LocalRootNode = Guid.Parse("0a0a0a0a-0000-7000-8000-000000000001");
    public static readonly Guid LocalSecondNode = Guid.Parse("0a0a0a0a-0000-7000-8000-000000000002");
    public static readonly Guid TeammateNode = Guid.Parse("0b0b0b0b-0000-7000-8000-00000000abcd");
    public static readonly Guid StrangerNode = Guid.Parse("0c0c0c0c-0000-7000-8000-0000000000ef");

    private static readonly DateTimeOffset Issued = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public static TrustChain Chain() => new(
        new Dictionary<string, TrustedOwner>
        {
            [LocalRoot] = new TrustedOwner(
                LocalRoot, "ssh-ed25519 AAAAFAKELOCAL local",
                [new TrustedNode(LocalSecondNode.ToString(), "ssh-ed25519 AAAAFAKESECOND second", "local-second-fp", Issued)],
                RootNodeId: LocalRootNode.ToString()),
            [TeammateRoot] = new TrustedOwner(
                TeammateRoot, "ssh-ed25519 AAAAFAKETEAM team",
                [new TrustedNode(TeammateNode.ToString(), "ssh-ed25519 AAAAFAKETEAMNODE node", "teammate-node-fp", Issued)]),
        },
        [
            new ProjectMember(LocalRoot, MembershipRole.Owner, Issued),
            new ProjectMember(TeammateRoot, MembershipRole.Member, Issued),
        ])
    {
        NodeDeclarations = new Dictionary<string, NodeGitHubDeclaration>
        {
            [TeammateNode.ToString()] = new(
                TeammateNode.ToString(), "teammate-node-fp", new DeclaredGitHubAccount(4242, "teammate-login"), Issued),
        },
    };

    /// <summary>The local owner's fleet, read out of <see cref="Chain"/> the way production reads it.</summary>
    public static Hall9k.Connectors.Prompts.LocalFleet Fleet() =>
        Hall9k.Connectors.Prompts.LocalFleet.Of(Chain(), LocalRoot)
        ?? throw new InvalidOperationException("The fixture chain names the local owner.");

    /// <summary>A fake event stamped the way <c>EventReplicationInbox</c> stamps an applied fact.</summary>
    public static FakeEvent<T> Replicated<T>(T data, Guid sender) where T : notnull
    {
        FakeEvent<T> @event = new(data);
        @event.SetHeader(ReplicationEventHeaders.OriginEventId, Guid.NewGuid().ToString());
        @event.SetHeader(ReplicationEventHeaders.OriginNodeId, sender.ToString());
        @event.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, sender.ToString());
        return @event;
    }
}
