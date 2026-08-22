using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Credentials;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What happens to the token a previous registration stored when the connection is pointed
/// somewhere else (PLAN.md §10).
/// <para>
/// The stored file is named from the site and the account precisely so that re-registering the
/// same account overwrites it, and that guarantee holds for exactly one shape of rotation. Move
/// the credential to an environment variable and the connection records <c>env:…</c> while the
/// file keeps a working token; re-register the same site as a different account and the derived
/// name changes, so the old account's token survives beside the new one. Either way a secret
/// nobody meant to keep sits on disk with nothing in <c>h9k connection list</c> mentioning it.
/// Origin incident (2026-08-21): the pre-PR review of the Jira branch traced both paths.
/// </para>
/// <para>
/// The same question is asked the other way round when a registration fails: the token is on disk
/// before the connection that points at it is recorded, so a commit that never lands leaves a
/// file whose fate depends on whether some connection was already reading it.
/// </para>
/// <para>
/// It needs the real projection because the last question the decision asks is whether any
/// connection still points at that file, and that is a query rather than a comparison. The query
/// is over every connection rather than this one, which is also why each test here stores under
/// its own file name: the fixture's database is shared, so a name reused across tests would make
/// one test's live credential look like another test's superseded one.
/// </para>
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ConnectionCredentialRotationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Site = new("https://hall9k.atlassian.net");

    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
    private readonly string _home = SetTempHome();

    /// <summary>
    /// The home the vault writes under, redirected before anything reads it. A field initializer
    /// rather than a constructor body, because a type with a primary constructor cannot declare
    /// one of its own.
    /// </summary>
    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public async Task A_token_rotated_into_an_environment_variable_does_not_stay_on_disk()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        CredentialReference stored = await CredentialVault.StoreAsync(
            "jira-rotated-to-a-variable", "alices-token", cts.Token);
        Guid connectionId = await RegisterAsync(store, "alice@corp.com", stored, cts.Token);

        CredentialReference rotated = CredentialReference.EnvironmentVariable("JIRA_API_TOKEN");
        CredentialReference? superseded = await ReregisterAsync(
            store, connectionId, "alice@corp.com", rotated, cts.Token);

        superseded.Should().Be(stored, "the connection reads the variable now and nothing reads the file");
        CredentialVault.Discard(superseded!).Should().Be(CredentialVault.FileFor(stored.Identifier!));
        File.Exists(CredentialVault.FileFor(stored.Identifier!)).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_account_at_the_same_site_does_not_leave_the_first_ones_token_behind()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        CredentialReference alice = await CredentialVault.StoreAsync(
            "jira-rotated-to-another-account-alice", "alices-token", cts.Token);
        Guid connectionId = await RegisterAsync(store, "alice@corp.com", alice, cts.Token);

        // The file name is derived from the account, so Bob's registration writes a new file
        // rather than overwriting Alice's.
        CredentialReference bob = await CredentialVault.StoreAsync(
            "jira-rotated-to-another-account-bob", "bobs-token", cts.Token);
        CredentialReference? superseded = await ReregisterAsync(
            store, connectionId, "bob@corp.com", bob, cts.Token);

        superseded.Should().Be(alice);
        CredentialVault.Discard(superseded!);
        File.Exists(CredentialVault.FileFor(alice.Identifier!)).Should().BeFalse();
        File.Exists(CredentialVault.FileFor(bob.Identifier!)).Should().BeTrue("the live credential is untouched");
    }

    [Fact]
    public async Task Re_registering_the_same_account_supersedes_nothing_because_the_file_was_overwritten()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        CredentialReference stored = await CredentialVault.StoreAsync(
            "jira-rotated-in-place", "alices-token", cts.Token);
        Guid connectionId = await RegisterAsync(store, "alice@corp.com", stored, cts.Token);

        // The ordinary token rotation: same site, same account, same file name, new contents.
        CredentialReference again = await CredentialVault.StoreAsync(
            "jira-rotated-in-place", "a-fresh-token", cts.Token);
        CredentialReference? superseded = await ReregisterAsync(
            store, connectionId, "alice@corp.com", again, cts.Token);

        superseded.Should().BeNull("deleting it would delete the credential just written");
        File.Exists(CredentialVault.FileFor(stored.Identifier!)).Should().BeTrue();
    }

    /// <summary>
    /// A first registration writes the token, then fails to commit. Nothing was ever recorded, so
    /// no connection points at the file and the command has to take it back off disk — otherwise
    /// a working API token sits in the credentials directory that nothing references and that
    /// <c>h9k connection list</c> does not mention.
    /// </summary>
    [Fact]
    public async Task A_registration_that_never_commits_does_not_leave_the_token_it_wrote()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await using IDocumentSession session = store.LightweightSession();

        // The command settles the reference before it writes, precisely so this question can be
        // asked while the session is still healthy — the failure is where it would not be.
        CredentialReference planned = CredentialReference.File("jira-registration-that-never-committed");
        bool pointedAt = await ConnectionAddJiraCommand.PointedAtAsync(session, planned, cts.Token);
        await CredentialVault.StoreAsync(planned.Identifier!, "alices-token", cts.Token);

        pointedAt.Should().BeFalse("nothing was recorded, so no connection reads what was written");
        CredentialVault.Discard(planned).Should().Be(CredentialVault.FileFor(planned.Identifier!));
        File.Exists(CredentialVault.FileFor(planned.Identifier!)).Should().BeFalse();
    }

    /// <summary>
    /// The same failure during an ordinary rotation, where the write overwrote the very file the
    /// registered connection reads through. The new token verified against the same site and
    /// account, so that connection still works; removing the file would break a registration this
    /// command never managed to change.
    /// </summary>
    [Fact]
    public async Task A_rotation_that_never_commits_keeps_the_file_the_connection_still_reads()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();

        CredentialReference stored = await CredentialVault.StoreAsync(
            "jira-rotation-that-never-committed", "alices-token", cts.Token);
        await RegisterAsync(store, "alice@corp.com", stored, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        bool pointedAt = await ConnectionAddJiraCommand.PointedAtAsync(session, stored, cts.Token);
        await CredentialVault.StoreAsync(stored.Identifier!, "a-fresh-token", cts.Token);

        pointedAt.Should().BeTrue("the registered connection reads that same file");
        File.Exists(CredentialVault.FileFor(stored.Identifier!)).Should().BeTrue();
    }

    /// <summary>
    /// What <c>h9k connection add jira</c> does: register, then read the connection back, then
    /// re-register it, then ask what the first registration left behind.
    /// </summary>
    private static async Task<Guid> RegisterAsync(
        DocumentStore store, string email, CredentialReference credential, CancellationToken cancellationToken)
    {
        Guid connectionId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, DomainId.New(), WorkItemProvider.Jira, email, credential, Now, Site));
        await session.SaveChangesAsync(cancellationToken);
        return connectionId;
    }

    private static async Task<CredentialReference?> ReregisterAsync(
        DocumentStore store,
        Guid connectionId,
        string email,
        CredentialReference credential,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ConnectionDetails existing = (await session.LoadAsync<ConnectionDetails>(connectionId, cancellationToken))!;
        ConnectionAggregate aggregate = (await session.Events
            .AggregateStreamAsync<ConnectionAggregate>(connectionId, token: cancellationToken))!;

        session.Events.Append(connectionId, ConnectionDecider.Reregister(
            aggregate, email, credential, Now, Site));
        await session.SaveChangesAsync(cancellationToken);

        return await ConnectionAddJiraCommand.SupersededCredentialAsync(
            session, existing, credential, cancellationToken);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
