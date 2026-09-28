using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Approves an owner-role member write a non-root node's own invite match asked this root to make
/// (idea 6be68ee2, companion 1bb803e1) — the door <c>h9k status</c>'s own "needs you" line names.
/// The root itself decides, deliberately never automatic the way a member-role write is: an
/// owner-role write hands the new member the identical write access this node's own root key
/// carries, so it waits for a human here even though the requesting node already matched the invite
/// on its own.
/// </summary>
public sealed class ProjectMemberApproveCommand : Hall9kAsyncCommand<ProjectMemberApproveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandArgument(1, "<INVITE-ID>")]
        [Description("The invite id h9k status prints beside the owner-role write it is holding for approval.")]
        public string InviteId { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
        if (!Guid.TryParse(settings.InviteId, out Guid inviteId))
        {
            throw new DomainValidationException(
                $"'{settings.InviteId}' is not a recognized invite id — h9k status prints the exact value to pass here.");
        }

        return await RunAsync(session, project, inviteId, cancellationToken);
    }

    internal static Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, Guid inviteId, CancellationToken cancellationToken) =>
        RunAsync(
            session, project, inviteId, new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new GitLedgerChainReader(),
            new NodeKeyStore(), cancellationToken);

    /// <summary>The whole approve flow, seamed on <see cref="ILedger"/>, <see cref="ILedgerChainReader"/>,
    /// and <see cref="NodeKeyStore"/> so a test drives it against fakes (Brian's 2026-09-13 testing rule).</summary>
    internal static async Task<int> RunAsync(
        IDocumentSession session, ProjectDetails project, Guid inviteId, ILedger ledger, ILedgerChainReader chainReader,
        NodeKeyStore keyStore, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        OwnerActHoldDetails? hold = await session.Query<OwnerActHoldDetails>()
            .Where(candidate => candidate.InviteId == inviteId && candidate.ProjectId == project.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (hold is null)
        {
            throw new DomainNotFoundException(
                $"No owner-role write held for invite {inviteId} in '{project.Name}' — h9k status prints the exact "
                + "invite id to pass here.");
        }

        if (hold.Approved)
        {
            throw new DomainValidationException($"The owner-role write for invite {inviteId} was already approved.");
        }

        if (hold.Expired)
        {
            throw new DomainValidationException(
                $"The owner-role write for invite {inviteId} already expired — the requesting node has moved on; "
                + "a fresh invite is needed.");
        }

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");
        string myRoot = owner.RootFingerprint
            ?? throw new DomainValidationException("This node has no root fingerprint yet — run h9k project join first.");
        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);

        // The identical root-only gate every other members-ref write in this task applies
        // (idea 6be68ee2, trust-ledger finding 2) — the hold was created while this node held a live
        // root key, but succession may have rotated a successor in since; a write this reader would
        // never recognize as authorized is worse than refusing to make it.
        TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        if (!chain.IsLiveRootKeyOfOwner(key.Fingerprint, myRoot))
        {
            throw new DomainValidationException(
                $"This node ({key.Fingerprint}) does not currently hold a live root key for owner {myRoot} — only "
                + $"the root itself may approve an owner-role member write. Re-run h9k project member approve "
                + $"{project.Name} {inviteId} from a node holding a root key for {myRoot}{RootNodeDescription.Of(chain, myRoot)}.");
        }

        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        string? commitId = await MemberVouchLedgerWriter.WriteAsync(
            ledger, hold.ProjectRepositoryPath, hold.CandidateOwnerFingerprint, hold.Role, hold.IssuedAt, committer,
            signingKey, cancellationToken);

        // Appended only once the ledger write it describes has actually landed, and only when this
        // project's own local Members mirror does not already agree — the identical ordering and
        // guard InviteSweepEngine.TryClaimAsync's own root-direct path and
        // OwnerActRequestWatchLoop.ReactToRequestAsync's own PerformMemberWrite case both apply to
        // the identical event (independent pre-PR review, cycle 1, conformance lens, medium): this
        // event feeds ProjectDetails.Members/ProjectAggregate.Members, so a human's own approval of
        // an owner-role hold must land it exactly the way the automatic member-role path does —
        // missing here left ProjectJoinStatus.NotJoined reading "not joined yet" for a member
        // admitted through this exact door too.
        if (!project.Members.TryGetValue(hold.CandidateOwnerFingerprint, out ProjectMemberRole? existingRole)
            || existingRole != hold.Role)
        {
            session.Events.Append(project.Id, ProjectDecider.VouchMember(project.Id, hold.CandidateOwnerFingerprint, hold.Role, now));
        }

        // The ledger write above can take long enough for the daemon's own OwnerActRequestWatchLoop
        // (ExpireStaleHoldsAsync, polling independently every PollInterval) to expire this exact hold
        // while this command is still running — the projection's own hold.Expired check above only
        // ruled that out as of the moment this command started, not as of now. The write already
        // landed either way, so OwnerActHoldDecider.Approve refusing to append onto an
        // already-expired hold must never also swallow the requester's own answer: the requester is
        // told done regardless of what this hold's own bookkeeping now says, never left holding an
        // owner-role grant that actually landed in the ledger while believing (from an earlier
        // expired reply, or no reply at all) that it needs to re-mint (independent pre-PR review,
        // cycle 1, adversarial lens, medium).
        StreamState fence = await session.Events.FetchStreamStateAsync(hold.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Owner-act hold {hold.Id} has no stream to approve.");
        OwnerActHoldAggregate aggregate = await session.Events.AggregateStreamAsync<OwnerActHoldAggregate>(
            hold.Id, version: fence.Version, token: cancellationToken)
            ?? throw new InvalidOperationException($"Owner-act hold {hold.Id} has no aggregate to approve.");
        if (aggregate.Expired)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning[/] the ten-minute wait closed this hold while this approval's own ledger write "
                + "was still landing — the write itself already succeeded, so the requesting node is told done "
                + "anyway.");
        }
        else if (!aggregate.Approved)
        {
            session.Events.Append(hold.Id, expectedVersion: fence.Version + 1, OwnerActHoldDecider.Approve(aggregate, commitId, now));
        }

        OwnerActEnvelopeCodec.OwnerActOutcomeRecord record = new(
            hold.InviteId, OwnerActEnvelopeCodec.OwnerActVerdict.Done, commitId, Reason: null);
        await MessageOutbox.QueueAsync(
            session, context.NodeId, project.Id, myRoot, MessageAudience.Node(hold.RequesterNodeId),
            hold.InviteId.ToString(), MessageKind.OwnerActOutcome, OwnerActEnvelopeCodec.Encode(record), now, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            $"[green]Approved[/] the owner-role write for invite [dim]{inviteId}[/] in '{project.Name.EscapeMarkup()}' — "
            + "the requesting node picks up the result on its own next sweep.");
        return ExitCodes.Ok;
    }
}
