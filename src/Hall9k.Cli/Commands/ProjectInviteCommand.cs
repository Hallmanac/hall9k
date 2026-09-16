using System.ComponentModel;
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
/// Mints a single-use, member-of-project invite secret (idea 202383dc, T2): "owner role invites
/// members". Written as <c>owners/&lt;minting-node's-own-root&gt;/invites/&lt;invite-id&gt;.yaml</c>
/// into this one project's own ledger — refused, before any push, unless this node's own root
/// currently holds the owner role in this project's own chain (the identical gate <c>h9k project
/// member remove</c> already applies). The secret is printed exactly once and kept only in this
/// node's own local store; it never reaches the ledger, which only ever carries its hash.
/// </summary>
public sealed class ProjectInviteCommand : Hall9kAsyncCommand<ProjectInviteCommand.Settings>
{
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--role <ROLE>")]
        [Description("The new member's own role once claimed: owner or member. Defaults to member.")]
        public string? Role { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        return await RunAsync(session, project, settings.Role, cancellationToken);
    }

    internal static Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, string? roleInput, CancellationToken cancellationToken) =>
        RunAsync(
            session, project, roleInput, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    /// <summary>The whole mint flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// and <see cref="NodeKeyStore"/> so a test drives it against fakes (Brian's 2026-09-13 testing rule).</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, string? roleInput, ILedger ledger,
        ILedgerChainReader chainReader, NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        ProjectMemberRole role = roleInput.IsBlank() ? ProjectMemberRole.Member : (ProjectMemberRole)roleInput;
        if (role != ProjectMemberRole.Owner && role != ProjectMemberRole.Member)
        {
            throw new DomainValidationException(
                $"--role must be {ProjectMemberRole.Owner} or {ProjectMemberRole.Member} (idea 202383dc: "
                + "\"roles are owner and member only\").");
        }

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

        TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        if (chain.RoleOf(myRoot) != MembershipRole.Owner || !chain.IsEnrolledInOwner(key.Fingerprint, myRoot))
        {
            throw new DomainValidationException(
                $"This node's own owner ({myRoot}) does not currently hold the owner role in "
                + $"'{project.Name}' — only an owner-role member's own node may mint a member-of-project "
                + "invite (idea 202383dc: \"owner role invites members\"). On a project that predates "
                + $"this chain, with no members yet recorded at all, h9k project join {project.Name} is "
                + "the step that establishes this node's own root as genesis; only then does this node "
                + "hold the owner role here and h9k project invite succeed.");
        }

        Guid inviteId = DomainId.New();
        string secret = InviteSecret.Generate(myRoot, inviteId);
        string secretHash = InviteSecret.Hash(secret);
        OperatingSettings configured = await PlatformConfigFile.ReadOperatingSettingsAsync(cancellationToken);
        DateTimeOffset expiresAt = now.AddHours(configured.InviteExpiryHours ?? OperatingSettings.DefaultInviteExpiryHours);
        InviteLedgerRecord record = new(secretHash, InviteClaimKind.MemberOfProject, role, expiresAt, Spent: false);

        await WriteInviteFileAsync(ledger, project.RepositoryPath, myRoot, inviteId, record, committer, signingKey, cancellationToken);

        session.Events.StartStream<InviteAggregate>(
            inviteId,
            InviteDecider.Mint(
                inviteId, context.NodeId, myRoot, InviteClaimKind.MemberOfProject, role, project.Id,
                project.RepositoryPath, secret, secretHash, now, expiresAt));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Minted[/] a member-of-project ({role.Value}) invite for '{project.Name.EscapeMarkup()}', "
            + $"expiring {expiresAt:u}.");
        AnsiConsole.MarkupLine($"[bold]Secret (shown once — copy it now):[/] {secret}");
        AnsiConsole.MarkupLine(
            "[dim]Give it to the new member. On their own node: h9k project join "
            + $"{project.Name.EscapeMarkup()} --invite <secret> — this node's own daemon sweep vouches it in "
            + "automatically once the proof appears, no further prompt needed.[/]");
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
            + "something else is writing it at the same time. Re-run h9k project invite once that settles.");
    }
}
