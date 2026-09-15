using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Mints a single-use, node-of-owner invite secret (idea 202383dc, T2): "any enrolled node
/// invites its own owner's nodes". Written as <c>owners/&lt;root&gt;/invites/&lt;invite-id&gt;.yaml</c>
/// into every non-archived project this owner is registered to, the identical "owner-wide, every
/// project" shape <c>h9k node vouch</c> already uses — refused per project, before any push, when
/// this node's own key is not itself currently enrolled in that owner's own chain there. The
/// secret is printed exactly once and kept only in this node's own local store
/// (<see cref="InviteAggregate.Secret"/>); it never reaches the ledger, which only ever carries
/// its hash.
/// </summary>
public sealed class NodeInviteCommand : Hall9kAsyncCommand<NodeInviteCommand.Settings>
{
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, cancellationToken);
    }

    internal static Task<int> RunAsync(IDocumentSession session, CancellationToken cancellationToken) =>
        RunAsync(
            session, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    /// <summary>The whole mint flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// and <see cref="NodeKeyStore"/> so a test drives it against fakes (Brian's 2026-09-13 testing rule).</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, ILedger ledger, ILedgerChainReader chainReader, NodeKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        if (owner.RootFingerprint is not { } myRoot)
        {
            throw new DomainValidationException("This node has no root fingerprint yet — run h9k project join first.");
        }

        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);
        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .Where(project => project.OwnerId == context.OwnerId && !project.IsArchived)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            throw new DomainValidationException("No project is registered to this owner yet — run h9k project join <project> first.");
        }

        Guid inviteId = DomainId.New();
        string secret = InviteSecret.Generate(myRoot, inviteId);
        string secretHash = InviteSecret.Hash(secret);
        OperatingSettings configured = await PlatformConfigFile.ReadOperatingSettingsAsync(cancellationToken);
        DateTimeOffset expiresAt = now.AddHours(configured.InviteExpiryHours ?? OperatingSettings.DefaultInviteExpiryHours);
        InviteLedgerRecord record = new(secretHash, InviteClaimKind.NodeOfOwner, Role: null, expiresAt, Spent: false);

        int wroteInto = 0;
        foreach (ProjectDetails project in projects)
        {
            try
            {
                TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
                if (!chain.IsEnrolledInOwner(key.Fingerprint, myRoot))
                {
                    throw new DomainValidationException(
                        $"This node ({key.Fingerprint}) is not currently enrolled in owner {myRoot}'s own chain in "
                        + $"'{project.Name}' — only an already-enrolled node may mint a node-of-owner invite "
                        + "(idea 202383dc: \"any enrolled node invites its own owner's nodes\").");
                }

                await WriteInviteFileAsync(ledger, project.RepositoryPath, myRoot, inviteId, record, committer, signingKey, cancellationToken);
                wroteInto++;
            }
            // A per-project refusal is a reason to skip that one project, never to abort a mint
            // that already landed in an earlier one — the identical NodeVouchCommand catch set,
            // for the identical reason (its own doc comment).
            catch (Exception exception)
                when (exception is LedgerPushRejectedException or InvalidOperationException
                    or DomainValidationException or DomainConflictException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not mint the invite in '{project.Name.EscapeMarkup()}' ({exception.Message.EscapeMarkup()}) — skipped.[/]");
            }
        }

        if (wroteInto == 0)
        {
            throw new DomainValidationException(
                "Could not mint an invite in any project — this node is not itself enrolled in this owner's own "
                + "chain anywhere it joined; see the messages above for which.");
        }

        session.Events.StartStream<InviteAggregate>(
            inviteId,
            InviteDecider.Mint(
                inviteId, context.NodeId, myRoot, InviteClaimKind.NodeOfOwner, role: null, projectId: null,
                projectRepositoryPath: null, secret, secretHash, now, expiresAt));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Minted[/] a node-of-owner invite, written into {wroteInto} project(s), expiring {expiresAt:u}.");
        AnsiConsole.MarkupLine($"[bold]Secret (shown once — copy it now):[/] {secret}");
        AnsiConsole.MarkupLine(
            "[dim]Give it to the new node. On it: h9k project join <project> --invite <secret> — this node's own "
            + "daemon sweep vouches it in automatically once the proof appears, no further prompt needed.[/]");
        return ExitCodes.Ok;
    }

    private static async Task WriteInviteFileAsync(
        ILedger ledger, string repositoryPath, string rootFingerprint, Guid inviteId, InviteLedgerRecord record,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = InviteLedgerRecord.RefName(rootFingerprint);
        string path = InviteLedgerRecord.PathFor(rootFingerprint, inviteId);
        string content = record.ToYaml();

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(repositoryPath, refName, path, content, current.BlobId, $"Mint invite {inviteId}", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this mint after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k node invite once that settles.");
    }
}
