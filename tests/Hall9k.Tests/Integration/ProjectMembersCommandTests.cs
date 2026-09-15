using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <c>h9k project members</c> and <c>h9k project member remove</c> (idea 202383dc, T1), against
/// real Marten/Postgres but a <see cref="FakeLedger"/>/<see cref="FakeLedgerChainReader"/> stand-in
/// for the ledger and the chain read (Brian's 2026-09-13 testing rule).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("Hall9kHome")]
public sealed class ProjectMembersCommandTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/does/not/matter/on/a/fake/ledger";
    private const string MembersRefName = "refs/hall9k/ledger/members";

    private readonly PostgresFixture _postgres;
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-project-members-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public ProjectMembersCommandTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Members_lists_the_chains_own_current_members()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { ["root-a"] = new("root-a", "ssh-ed25519 AAA root-a", []) },
            [new ProjectMember("root-a", MembershipRole.Owner, Now)]);

        await using IQuerySession session = _postgres.Store.QuerySession();
        int exitCode = await ProjectMembersCommand.RunAsync(
            session, new ProjectMembersCommand.Settings { Project = project.Name }, new FakeLedgerChainReader(chain),
            CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
    }

    [Fact]
    public async Task Remove_refuses_before_any_push_when_this_owner_does_not_hold_the_owner_role()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);

        // This owner's own root exists in the chain, but only as a plain member, never owner.
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Member, Now)]));

        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = new string('a', 64) };
        Func<Task> act = () => ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>().WithMessage("*owner role*");
        ledger.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_deletes_the_member_file_when_this_owner_holds_the_owner_role()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();
        string targetFingerprint = new string('b', 64);
        await SeedMemberFileAsync(ledger, targetFingerprint);

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = targetFingerprint };
        int exitCode = await ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        exitCode.Should().Be(ExitCodes.Ok);
        ledger.Deletes.Should().ContainSingle(d => d.RefName == MembersRefName && d.Path == $"members/{targetFingerprint}.yaml");

        ProjectDetails updated = (await session.LoadAsync<ProjectDetails>(project.Id, CancellationToken.None))!;
        updated.Members.Should().NotContainKey(targetFingerprint);
    }

    [Fact]
    public async Task Remove_refuses_a_fingerprint_that_is_not_currently_a_member()
    {
        ProjectDetails project = await SeedProjectAsync(CancellationToken.None);
        FakeLedger ledger = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();
        (string myFingerprint, string myPublicKeyLine) = await EstablishOwnRootAsync(session, CancellationToken.None);
        FakeLedgerChainReader chainReader = new(new TrustChain(
            new Dictionary<string, TrustedOwner> { [myFingerprint] = new(myFingerprint, myPublicKeyLine, []) },
            [new ProjectMember(myFingerprint, MembershipRole.Owner, Now)]));

        ProjectMemberRemoveCommand.Settings settings = new() { Project = project.Name, Fingerprint = new string('c', 64) };
        Func<Task> act = () => ProjectMemberRemoveCommand.RunAsync(session, settings, ledger, chainReader, new NodeKeyStore(), CancellationToken.None);

        await act.Should().ThrowAsync<DomainValidationException>();
        ledger.Deletes.Should().BeEmpty();
    }

    private static async Task SeedMemberFileAsync(FakeLedger ledger, string fingerprint)
    {
        string content = $"root_fingerprint: \"{fingerprint}\"\nrole: \"owner\"\nissued_at: \"2026-09-01T00:00:00Z\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, MembersRefName, $"members/{fingerprint}.yaml", content, ExpectedBlobId: null,
                "seed member", new LedgerCommitter("Test", "test@test.local"), new LedgerSigningKey("/does/not/matter/key")),
            CancellationToken.None);
    }

    private async Task<(string Fingerprint, string PublicKeyLine)> EstablishOwnRootAsync(
        IDocumentSession session, CancellationToken cancellationToken)
    {
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        NodeSigningKey key = await new NodeKeyStore().EnsureAsync(context.NodeId, cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException("Owner bootstrap did not create an owner stream.");
        session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, key.Fingerprint, verified: true, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (key.Fingerprint, key.PublicKeyLine);
    }

    private async Task<ProjectDetails> SeedProjectAsync(CancellationToken cancellationToken)
    {
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(_postgres.Store, cancellationToken);

        await using IDocumentSession bootstrapSession = _postgres.Store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(bootstrapSession, cancellationToken);
        await bootstrapSession.SaveChangesAsync(cancellationToken);

        Guid projectId = DomainId.New();
        await using IDocumentSession projectSession = _postgres.Store.LightweightSession();
        projectSession.Events.StartStream<ProjectAggregate>(
            projectId,
            ProjectDecider.Register(
                projectId, context.OwnerId, DomainId.New(), "smoke", RepositoryPath, null, null, Now));
        await projectSession.SaveChangesAsync(cancellationToken);

        return (await projectSession.LoadAsync<ProjectDetails>(projectId, cancellationToken))!;
    }
}
