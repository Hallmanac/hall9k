using System.Globalization;
using System.Text;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Daemon.Invites;

/// <summary>How many invites this sweep actually vouched in, for the loop's own log line.</summary>
public sealed record InviteSweepResult(int InvitesSpent);

/// <summary>
/// The minting node's own invite sweep (idea 202383dc, T2): for every invite this node minted and
/// has not yet marked spent, scans the candidate project(s) it applies to for a node file carrying
/// a proof, and vouches the first one whose proof actually matches — HMAC(secret, that node's own
/// key fingerprint) equal to what it wrote into its own <c>invite_proof</c> field. A wrong proof
/// never matches (there is nothing to recover from being wrong — it simply never equals the real
/// HMAC), so nothing is vouched for it; a spent or expired invite is filtered out of the
/// "outstanding" query before any candidate is even read, so neither is ever re-processed. Needs no
/// <see cref="ILedgerChainReader"/> at all: HMAC possession of the secret is the whole proof, prior
/// to and independent of any chain trust — the vouch this sweep writes is what BEGINS that trust,
/// not something checked against it first.
/// </summary>
public sealed class InviteSweepEngine(
    IDocumentStore store, NodeContext node, ILedger ledger, NodeKeyStore keyStore, ILogger<InviteSweepEngine> logger)
{
    private const int MaxConflictRetries = 5;

    public async Task<InviteSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();

        IReadOnlyList<InviteDetails> outstanding = await session.Query<InviteDetails>()
            .Where(invite => invite.MinterNodeId == node.NodeId && !invite.Spent && invite.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        if (outstanding.Count == 0)
        {
            return new InviteSweepResult(0);
        }

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(node.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No owner {node.OwnerId} for the invite sweep's own node.");
        NodeSigningKey key = await keyStore.EnsureAsync(node.NodeId, cancellationToken);
        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{node.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        int spent = 0;
        foreach (InviteDetails invite in outstanding)
        {
            try
            {
                if (await TryClaimAsync(session, invite, owner, committer, signingKey, now, cancellationToken))
                {
                    spent++;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Invite sweep failed for invite {InviteId}; will retry next sweep", invite.Id);
            }
        }

        return new InviteSweepResult(spent);
    }

    private async Task<bool> TryClaimAsync(
        IDocumentSession session, InviteDetails invite, OwnerAggregate owner, LedgerCommitter committer,
        LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        InviteAggregate? aggregate = await session.Events.AggregateStreamAsync<InviteAggregate>(invite.Id, token: cancellationToken);
        if (aggregate is null || aggregate.Spent)
        {
            return false;
        }

        IReadOnlyList<ProjectDetails> projects = await TargetProjectsAsync(session, aggregate, cancellationToken);
        foreach (ProjectDetails project in projects)
        {
            (Guid CandidateNodeId, string CandidateFingerprint, string CandidateOwnerFingerprint, string PublicKeyLine)? match =
                await FindMatchAsync(project.RepositoryPath, aggregate.Secret, cancellationToken);
            if (match is not { } candidate)
            {
                continue;
            }

            if (aggregate.Claim == InviteClaimKind.NodeOfOwner)
            {
                await VouchNodeAsync(
                    project.RepositoryPath, aggregate.MinterOwnerFingerprint, candidate.CandidateNodeId,
                    candidate.PublicKeyLine, now, committer, signingKey, cancellationToken);
                session.Events.Append(
                    node.OwnerId, OwnerDecider.VouchNode(owner, candidate.CandidateNodeId, candidate.CandidateFingerprint, now));
            }
            else
            {
                ProjectMemberRole role = aggregate.Role
                    ?? throw new InvalidOperationException($"Invite {aggregate.Id} is member-of-project but carries no role.");
                await VouchMemberAsync(
                    project.RepositoryPath, candidate.CandidateOwnerFingerprint, role, now, committer, signingKey, cancellationToken);
                session.Events.Append(
                    project.Id, ProjectDecider.VouchMember(project.Id, candidate.CandidateOwnerFingerprint, role, now));
            }

            await MarkInviteSpentInLedgerAsync(
                project.RepositoryPath, aggregate.MinterOwnerFingerprint, aggregate.Id, aggregate.SecretHash, aggregate.Claim,
                aggregate.Role, aggregate.ExpiresAt, committer, signingKey, cancellationToken);
            session.Events.Append(
                aggregate.Id,
                InviteDecider.Spend(aggregate, candidate.CandidateNodeId, candidate.CandidateOwnerFingerprint, now));
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Every non-archived project this invite applies to — every one the minting owner is
    /// registered to for a <see cref="InviteClaimKind.NodeOfOwner"/> invite (the same "owner-wide,
    /// every project" shape it was minted into), or exactly the one it was minted for
    /// (<see cref="InviteAggregate.ProjectId"/>) for a <see cref="InviteClaimKind.MemberOfProject"/>
    /// one, skipped if that project was archived since minting.
    /// </summary>
    private async Task<IReadOnlyList<ProjectDetails>> TargetProjectsAsync(
        IDocumentSession session, InviteAggregate invite, CancellationToken cancellationToken)
    {
        if (invite.Claim == InviteClaimKind.MemberOfProject)
        {
            if (invite.ProjectId is not { } projectId)
            {
                return [];
            }

            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
            return project is { IsArchived: false } ? [project] : [];
        }

        return await session.Query<ProjectDetails>()
            .Where(project => project.OwnerId == node.OwnerId && !project.IsArchived)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Scans every node ref in <paramref name="repositoryPath"/> for the first one whose own
    /// <c>invite_proof</c> field equals <see cref="InviteSecret.ComputeProof"/> of
    /// <paramref name="secret"/> and that candidate's own <c>key_fingerprint</c> — a wrong proof
    /// (any other value, including one belonging to a different outstanding invite entirely) never
    /// matches and is simply skipped.
    /// </summary>
    private async Task<(Guid CandidateNodeId, string CandidateFingerprint, string CandidateOwnerFingerprint, string PublicKeyLine)?> FindMatchAsync(
        string repositoryPath, string secret, CancellationToken cancellationToken)
    {
        const string nodesPrefix = "refs/hall9k/ledger/nodes/";
        IReadOnlyList<string> nodeRefs = await ledger.ListRefsAsync(repositoryPath, nodesPrefix, cancellationToken);
        foreach (string refName in nodeRefs)
        {
            if (!Guid.TryParse(refName[nodesPrefix.Length..], out Guid candidateNodeId))
            {
                continue;
            }

            string path = $"nodes/{candidateNodeId}/node.yaml";
            LedgerFile file = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (!file.Exists || file.Content is not { } content)
            {
                continue;
            }

            string? proof = ExtractQuotedYamlValue(content, "invite_proof");
            string? publicKeyLine = ExtractQuotedYamlValue(content, "public_key");
            string? ownerFingerprint = ExtractQuotedYamlValue(content, "owner_fingerprint");
            if (proof.IsBlank() || publicKeyLine.IsBlank() || ownerFingerprint.IsBlank())
            {
                continue;
            }

            string candidateFingerprint = NodeKeyStore.Fingerprint(publicKeyLine);
            if (string.Equals(proof, InviteSecret.ComputeProof(secret, candidateFingerprint), StringComparison.Ordinal))
            {
                return (candidateNodeId, candidateFingerprint, ownerFingerprint, publicKeyLine);
            }
        }

        return null;
    }

    private async Task VouchNodeAsync(
        string repositoryPath, string rootFingerprint, Guid candidateNodeId, string publicKeyLine, DateTimeOffset now,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{rootFingerprint}";
        string path = $"owners/{rootFingerprint}/nodes/{candidateNodeId}.yaml";
        string content = BuildYaml(
            ("node_id", candidateNodeId.ToString()),
            ("public_key", publicKeyLine),
            ("issued_at", now.ToString("o", CultureInfo.InvariantCulture)));
        await WriteWithRetryAsync(repositoryPath, refName, path, content, $"Vouch node {candidateNodeId} (invite)", committer, signingKey, cancellationToken);
    }

    private async Task VouchMemberAsync(
        string repositoryPath, string candidateOwnerFingerprint, ProjectMemberRole role, DateTimeOffset now,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        const string refName = "refs/hall9k/ledger/members";
        string path = $"members/{candidateOwnerFingerprint}.yaml";
        string content = BuildYaml(
            ("root_fingerprint", candidateOwnerFingerprint),
            ("role", role.Value),
            ("issued_at", now.ToString("o", CultureInfo.InvariantCulture)));
        await WriteWithRetryAsync(repositoryPath, refName, path, content, $"Vouch member {candidateOwnerFingerprint} (invite)", committer, signingKey, cancellationToken);
    }

    private async Task MarkInviteSpentInLedgerAsync(
        string repositoryPath, string rootFingerprint, Guid inviteId, string secretHash, InviteClaimKind claim,
        ProjectMemberRole? role, DateTimeOffset expiresAt, LedgerCommitter committer, LedgerSigningKey signingKey,
        CancellationToken cancellationToken)
    {
        InviteLedgerRecord spentRecord = new(secretHash, claim, role, expiresAt, Spent: true);
        await WriteWithRetryAsync(
            repositoryPath, InviteLedgerRecord.RefName(rootFingerprint), InviteLedgerRecord.PathFor(rootFingerprint, inviteId),
            spentRecord.ToYaml(), $"Mark invite {inviteId} spent", committer, signingKey, cancellationToken);
    }

    private async Task WriteWithRetryAsync(
        string repositoryPath, string refName, string path, string content, string commitMessage,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(repositoryPath, refName, path, content, current.BlobId, commitMessage, committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"{path} kept changing out from under the invite sweep after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time; will retry next sweep.");
    }

    private static string BuildYaml(params (string Key, string Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in fields)
        {
            builder.Append(key).Append(": \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"");
        }

        return builder.ToString();
    }

    /// <summary>Mirrors <c>GitLedgerChainReader.ExtractQuotedYamlValue</c>'s own small, flat reader.</summary>
    private static string? ExtractQuotedYamlValue(string yaml, string key)
    {
        foreach (string rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string prefix = $"{key}: \"";
            if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith('"'))
            {
                continue;
            }

            string inner = line[prefix.Length..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return null;
    }
}
