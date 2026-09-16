using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Removes a root fingerprint's project membership (idea 202383dc, T1) — deletes
/// <c>members/&lt;fingerprint&gt;.yaml</c> from <c>refs/hall9k/ledger/members</c>, a genuine tree
/// deletion rather than a tombstone. Refused, before any push, unless this node's own root
/// currently holds the owner role in this project's own chain.
/// </summary>
public sealed class ProjectMemberRemoveCommand : Hall9kAsyncCommand<ProjectMemberRemoveCommand.Settings>
{
    private const int MaxConflictRetries = 5;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<FINGERPRINT>")]
        [Description("The member's own root fingerprint — h9k project members prints it.")]
        public string Fingerprint { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    internal static Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken) =>
        RunAsync(
            session, settings, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    internal static async Task<int> RunAsync(
        IDocumentSession session, Settings settings, ILedger ledger, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        if (settings.Fingerprint.IsBlank() || !NodeKeyStore.IsFingerprint(settings.Fingerprint))
        {
            throw new DomainValidationException(
                $"'{settings.Fingerprint}' is not a fingerprint — h9k project members prints the exact value to pass here.");
        }

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
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

        TrustChain chain;
        try
        {
            chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        }
        // GitLedgerChainReader throws on a genuine network or credential failure rather than
        // folding it into an empty chain — reported here with the real cause, the identical
        // handling ProjectMembersCommand's own read already applies (independent pre-PR review,
        // cycle 1, adversarial lens, medium: this call sat outside any catch, so the process died
        // on an unhandled exception instead of a mapped exit code and an explanation).
        catch (InvalidOperationException exception)
        {
            throw new DomainValidationException(
                $"Could not read '{project.Name}'s own ledger chain: {exception.Message} Re-run "
                + $"h9k project member remove {project.Name} {settings.Fingerprint} once the remote "
                + "is reachable again.");
        }

        if (chain.RoleOf(myRoot) != MembershipRole.Owner || !chain.IsEnrolledInOwner(key.Fingerprint, myRoot))
        {
            throw new DomainValidationException(
                $"This node's own owner ({myRoot}) does not currently hold the owner role in "
                + $"'{project.Name}' — only an owner-role member's own node may remove a member "
                + "(idea 202383dc: \"written by a node whose owner holds the owner role\").");
        }

        // Refused before any push: removing the project's only owner-role member would leave no
        // signer able to authorize any future members-ref write at all — every subsequent write,
        // including a replacement owner, fails ComputeMembersAsync's own authorization loop for
        // want of any Owner-role member left to check against, freezing membership permanently
        // with no recovery path through this CLI (independent pre-PR review, cycle 1, adversarial
        // lens, medium).
        if (chain.RoleOf(settings.Fingerprint) == MembershipRole.Owner
            && chain.Members.Count(member => member.Role == MembershipRole.Owner) == 1)
        {
            throw new DomainValidationException(
                $"'{settings.Fingerprint}' is '{project.Name}'s only owner-role member — removing it "
                + "would leave no owner-role member able to authorize any future write to the members "
                + "ref, permanently freezing project membership. Make another root an owner first.");
        }

        string refName = "refs/hall9k/ledger/members";
        string path = $"members/{settings.Fingerprint}.yaml";

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(project.RepositoryPath, refName, path, cancellationToken);
            if (!current.Exists)
            {
                throw new DomainValidationException($"'{settings.Fingerprint}' is not currently a verified member of '{project.Name}'.");
            }

            LedgerWriteOutcome outcome = await ledger.DeleteAsync(
                new LedgerDeleteRequest(
                    project.RepositoryPath, refName, path, current.BlobId,
                    $"Remove member {settings.Fingerprint}", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                session.Events.Append(project.Id, ProjectDecider.RemoveMember(project.Id, settings.Fingerprint, now));
                await session.SaveChangesAsync(cancellationToken);

                AnsiConsole.MarkupLine(
                    $"[green]Removed[/] member [dim]{settings.Fingerprint}[/] from '{project.Name.EscapeMarkup()}'.");
                return ExitCodes.Ok;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this removal after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k project member remove once that settles.");
    }
}
