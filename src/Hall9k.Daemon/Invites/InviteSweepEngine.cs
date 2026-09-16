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
using Hall9k.Domain.Shared.Exceptions;
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
    private const string NodesRefPrefix = "refs/hall9k/ledger/nodes/";
    private const string MembersRefName = "refs/hall9k/ledger/members";

    /// <summary>Every node ref's own tip as of this node's last look, together with whatever
    /// candidate that read produced (or <c>null</c> if it carried none) — so a sweep that finds an
    /// unmoved tip skips re-reading the file but still re-offers the same candidate, rather than
    /// dropping it. In-memory and per-process by design: a restart just re-reads every node ref
    /// once, which is cheap and correct, never lossy (independent pre-PR review, cycle 1,
    /// conformance and adversarial lenses, both medium: an outstanding invite otherwise re-fetches
    /// every node ref in every target project, once per invite, on every tick). Caching the
    /// candidate itself, not only the tip, is what independent pre-PR review, cycle 2, adversarial
    /// lens, high flagged: recording only the tip made a candidate whose vouch/spend write failed
    /// partway through a tick vanish from every later tick's own candidate list forever, because its
    /// node ref's tip never moves again after the join — the same failure mode
    /// <c>MessageSweepEngine</c>'s own doc comment on <c>StalledAtSeq</c> warns against, but for a
    /// read that fully succeeded rather than one that did not.</summary>
    private readonly Dictionary<(string RepositoryPath, string RefName), (string Sha, CandidateNode? Candidate)> _lastKnownRefs = [];

    private sealed record CandidateNode(Guid NodeId, string KeyFingerprint, string OwnerFingerprint, string PublicKeyLine, string Proof);

    public async Task<InviteSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<InviteDetails> outstanding;
        await using (IDocumentSession querySession = store.LightweightSession())
        {
            outstanding = await querySession.Query<InviteDetails>()
                .Where(invite => invite.MinterNodeId == node.NodeId && !invite.Spent && invite.ExpiresAt > now)
                .ToListAsync(cancellationToken);
        }

        if (outstanding.Count == 0)
        {
            return new InviteSweepResult(0);
        }

        NodeSigningKey key = await keyStore.EnsureAsync(node.NodeId, cancellationToken);
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        // Populated at most once per project for this whole tick, however many outstanding invites
        // target it — the candidate scan itself does not vary by invite, only the proof match at
        // the end of it does (independent pre-PR review, cycle 1, adversarial lens, medium: the
        // scan was previously re-run from scratch per invite rather than once per project).
        Dictionary<string, IReadOnlyList<CandidateNode>> projectCandidates = [];

        int spent = 0;
        foreach (InviteDetails invite in outstanding)
        {
            try
            {
                if (await TryClaimAsync(invite, signingKey, projectCandidates, now, cancellationToken))
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

    /// <summary>
    /// One invite's own attempt, on its own <see cref="IDocumentSession"/> — never shared with any
    /// other invite in the same tick, so a failure partway through this one (a ledger write that
    /// exhausts its retries after an earlier event in this same attempt already appended) can never
    /// leave an unrelated invite's own <see cref="IDocumentSession.SaveChangesAsync"/> flushing this
    /// attempt's own half-applied events alongside it (independent pre-PR review, cycle 1,
    /// conformance lens, low).
    /// </summary>
    private async Task<bool> TryClaimAsync(
        InviteDetails invite, LedgerSigningKey signingKey, Dictionary<string, IReadOnlyList<CandidateNode>> projectCandidates,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        InviteAggregate? aggregate = await session.Events.AggregateStreamAsync<InviteAggregate>(invite.Id, token: cancellationToken);
        if (aggregate is null || aggregate.Spent)
        {
            return false;
        }

        IReadOnlyList<ProjectDetails> projects = await TargetProjectsAsync(session, aggregate, cancellationToken);
        if (projects.Count == 0)
        {
            return false;
        }

        CandidateNode? matched = null;
        foreach (ProjectDetails project in projects)
        {
            IReadOnlyList<CandidateNode> candidates = await GetProjectCandidatesAsync(project.RepositoryPath, projectCandidates, cancellationToken);
            matched = FindMatch(candidates, aggregate.Secret);
            if (matched is not null)
            {
                break;
            }
        }

        if (matched is not { } candidate)
        {
            return false;
        }

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(node.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No owner {node.OwnerId} for the invite sweep's own node.");
        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{node.NodeId}@hall9k.local");

        ProjectMemberRole? role = null;
        if (aggregate.Claim == InviteClaimKind.MemberOfProject)
        {
            role = aggregate.Role ?? throw new InvalidOperationException($"Invite {aggregate.Id} is member-of-project but carries no role.");
        }

        // Written into every target project, not only the one the match happened to be found in —
        // a node-of-owner invite's own record was minted into every non-archived project the owner
        // is registered to, so leaving the others unspent lets a leaked secret still be honored
        // there (independent pre-PR review, cycle 1, conformance and adversarial lenses, both
        // medium). Left unspent locally until every target project has actually landed the write:
        // a partial failure this tick is retried next tick rather than abandoned. Re-running an
        // already-succeeded project really is a cheap no-op: VouchNodeAsync/VouchMemberAsync tag
        // their own write with this invite's own id and reuse whatever issued_at a matching prior
        // write already carries, so a retry reproduces byte-identical content and
        // WriteWithRetryAsync's own read-before-write short-circuits rather than pushing a
        // redundant commit (independent pre-PR review, cycle 4, adversarial lens, medium — the
        // claim was false while issued_at was stamped fresh on every attempt).
        int wroteInto = 0;
        foreach (ProjectDetails project in projects)
        {
            try
            {
                bool proceeded;
                if (aggregate.Claim == InviteClaimKind.NodeOfOwner)
                {
                    proceeded = await VouchNodeAsync(
                        project.RepositoryPath, aggregate.Id, aggregate.MinterOwnerFingerprint, candidate.NodeId,
                        candidate.PublicKeyLine, now, committer, signingKey, cancellationToken);
                    if (!proceeded)
                    {
                        // Mirrors the member-of-project guard just below: the candidate's own ref
                        // name (so its node id) is self-chosen, never verified against anything this
                        // sweep can check, so accepting it unconditionally would let an invite
                        // holder plant their own key under an already-enrolled node's own id and
                        // silently evict it — a node-of-owner invite is for a NEW node, never a
                        // route to rewrite an existing one (independent pre-PR review, cycle 4,
                        // adversarial lens, medium).
                        logger.LogWarning(
                            "Invite {InviteId} matched a proof claiming node id {NodeId}, which is already enrolled "
                            + "under a different key in project {ProjectId} — refusing to overwrite an existing "
                            + "node; will retry next sweep.",
                            aggregate.Id, candidate.NodeId, project.Id);
                        continue;
                    }
                }
                else
                {
                    // The candidate's own owner_fingerprint field is self-declared in its node
                    // file, never verified against anything this sweep can check — the HMAC proof
                    // binds only the candidate's key fingerprint, never that claim. Refusing to
                    // grant membership at a fingerprint that is already a member (under a different
                    // invite, or none at all) is what stands between that unverified claim and an
                    // invite holder silently overwriting (and so demoting) an existing owner or
                    // member — a member-of-project invite is for a NEW member, never a route to
                    // rewrite an existing one (independent pre-PR review, cycle 1, adversarial
                    // lens, high). Tagging the write with this invite's own id, rather than merely
                    // checking whether the path exists, is what lets a later retry of this exact
                    // invite's own earlier, partially-completed attempt tell "I already wrote this"
                    // apart from "someone else already owns this" — a blind existence check wedges
                    // permanently the moment its own successful write is what it then sees on retry
                    // (independent pre-PR review, cycle 4, both lenses, high/medium).
                    proceeded = await VouchMemberAsync(
                        project.RepositoryPath, aggregate.Id, candidate.OwnerFingerprint, role!.Value, now, committer,
                        signingKey, cancellationToken);
                    if (!proceeded)
                    {
                        logger.LogWarning(
                            "Invite {InviteId} matched a proof claiming owner fingerprint {OwnerFingerprint}, which is "
                            + "already a member of project {ProjectId} — refusing to overwrite an existing member; "
                            + "will retry next sweep.",
                            aggregate.Id, candidate.OwnerFingerprint, project.Id);
                        continue;
                    }

                    session.Events.Append(
                        project.Id, ProjectDecider.VouchMember(project.Id, candidate.OwnerFingerprint, role.Value, now));
                }

                await MarkInviteSpentInLedgerAsync(
                    project.RepositoryPath, aggregate.MinterOwnerFingerprint, aggregate.Id, aggregate.SecretHash, aggregate.Claim,
                    aggregate.Role, aggregate.ExpiresAt, committer, signingKey, cancellationToken);
                wroteInto++;
            }
            // A per-project failure is a reason to retry that one project next sweep, never to
            // abandon a write that already landed in an earlier one — the identical
            // NodeVouchCommand/h9k project join catch set, for the identical reason (their own doc
            // comments).
            catch (Exception exception)
                when (exception is LedgerPushRejectedException or InvalidOperationException
                    or DomainValidationException or DomainConflictException)
            {
                logger.LogWarning(
                    exception, "Could not write invite {InviteId}'s vouch/spend into project {ProjectId}; will retry next sweep",
                    aggregate.Id, project.Id);
            }
        }

        if (wroteInto < projects.Count)
        {
            return false;
        }

        if (aggregate.Claim == InviteClaimKind.NodeOfOwner)
        {
            session.Events.Append(node.OwnerId, OwnerDecider.VouchNode(owner, candidate.NodeId, candidate.KeyFingerprint, now));
        }

        session.Events.Append(aggregate.Id, InviteDecider.Spend(aggregate, candidate.NodeId, candidate.OwnerFingerprint, now));
        await session.SaveChangesAsync(cancellationToken);
        return true;
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
    /// Every node ref in <paramref name="repositoryPath"/> that actually carries a usable candidate
    /// — a well-formed <c>invite_proof</c>, <c>public_key</c>, and <c>owner_fingerprint</c> — scanned
    /// at most once per sweep tick regardless of how many outstanding invites target this project
    /// (the cache in <see cref="SweepOnceAsync"/>), and skipping the file fetch for any node ref
    /// whose tip <see cref="_lastKnownRefs"/> already saw unchanged, reusing that ref's own
    /// previously-resolved candidate (or lack of one) instead: that ref's content, and so whatever
    /// proof it does or does not carry, cannot have changed either (independent pre-PR review,
    /// cycle 1, conformance and adversarial lenses, both medium). Reusing the cached candidate
    /// rather than treating an unmoved tip as "nothing here" is what keeps a candidate whose earlier
    /// vouch/spend write failed partway through still offered to every later tick — its node ref's
    /// tip never moves again after the join, so a naive skip would otherwise drop it forever
    /// (independent pre-PR review, cycle 2, adversarial lens, high).
    /// </summary>
    private async Task<IReadOnlyList<CandidateNode>> GetProjectCandidatesAsync(
        string repositoryPath, Dictionary<string, IReadOnlyList<CandidateNode>> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(repositoryPath, out IReadOnlyList<CandidateNode>? cached))
        {
            return cached;
        }

        IReadOnlyList<LedgerRef> nodeRefs = await ledger.ListRefsAsync(repositoryPath, NodesRefPrefix, cancellationToken);
        List<CandidateNode> candidates = [];
        foreach (LedgerRef nodeRef in nodeRefs)
        {
            if (!Guid.TryParse(nodeRef.RefName[NodesRefPrefix.Length..], out Guid candidateNodeId))
            {
                continue;
            }

            (string RepositoryPath, string RefName) tipKey = (repositoryPath, nodeRef.RefName);
            if (_lastKnownRefs.TryGetValue(tipKey, out (string Sha, CandidateNode? Candidate) known) && known.Sha == nodeRef.Sha)
            {
                if (known.Candidate is { } cachedCandidate)
                {
                    candidates.Add(cachedCandidate);
                }

                continue;
            }

            string path = $"nodes/{candidateNodeId}/node.yaml";
            LedgerFile file = await ledger.ReadAsync(repositoryPath, nodeRef.RefName, path, cancellationToken);
            if (!file.Exists || file.Content is not { } content)
            {
                _lastKnownRefs[tipKey] = (nodeRef.Sha, null);
                continue;
            }

            string? proof = ExtractQuotedYamlValue(content, "invite_proof");
            string? publicKeyLine = ExtractQuotedYamlValue(content, "public_key");
            string? ownerFingerprint = ExtractQuotedYamlValue(content, "owner_fingerprint");
            if (proof.IsBlank() || publicKeyLine.IsBlank() || ownerFingerprint.IsBlank())
            {
                _lastKnownRefs[tipKey] = (nodeRef.Sha, null);
                continue;
            }

            // Both guards turn an untrusted, ledger-sourced field into "not a candidate" rather
            // than a thrown exception or a blindly-trusted value: a malformed public key line must
            // never abort the scan for every other node ref behind it (independent pre-PR review,
            // cycle 1, conformance lens, medium — anyone with push on the repository can otherwise
            // poison every sweep of this invite until it expires), and a malformed owner fingerprint
            // must never be handed to a ledger path unvalidated (adversarial lens, high).
            if (!TryFingerprint(publicKeyLine, out string candidateFingerprint) || !NodeKeyStore.IsFingerprint(ownerFingerprint))
            {
                _lastKnownRefs[tipKey] = (nodeRef.Sha, null);
                continue;
            }

            CandidateNode candidate = new(candidateNodeId, candidateFingerprint, ownerFingerprint, publicKeyLine, proof);
            _lastKnownRefs[tipKey] = (nodeRef.Sha, candidate);
            candidates.Add(candidate);
        }

        cache[repositoryPath] = candidates;
        return candidates;
    }

    private static CandidateNode? FindMatch(IReadOnlyList<CandidateNode> candidates, string secret)
    {
        foreach (CandidateNode candidate in candidates)
        {
            if (string.Equals(candidate.Proof, InviteSecret.ComputeProof(secret, candidate.KeyFingerprint), StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes (or, on a retry of this exact invite's own earlier attempt, cheaply confirms)
    /// <c>owners/&lt;rootFingerprint&gt;/nodes/&lt;candidateNodeId&gt;.yaml</c>. Returns <c>false</c>,
    /// writing nothing, when a file already lives at that path and its own <c>invite_id</c> field
    /// does not name <paramref name="inviteId"/> — an already-enrolled node under this id, vouched
    /// by anything other than this exact invite (a different invite, a direct <c>h9k node vouch</c>,
    /// or the plain join genesis path, none of which stamp that field). The tag, not a bare
    /// existence check, is what tells "this invite already wrote this" apart from "something else
    /// owns this": a bare existence check sees an identical file either way, and so would refuse
    /// its own successful prior write forever on retry (independent pre-PR review, cycle 4, both
    /// lenses, high/medium).
    /// </summary>
    private async Task<bool> VouchNodeAsync(
        string repositoryPath, Guid inviteId, string rootFingerprint, Guid candidateNodeId, string publicKeyLine,
        DateTimeOffset now, LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{rootFingerprint}";
        string path = $"owners/{rootFingerprint}/nodes/{candidateNodeId}.yaml";
        LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (current.Exists && current.Content is { } existing
            && ExtractQuotedYamlValue(existing, "invite_id") != inviteId.ToString())
        {
            return false;
        }

        // Reuses whatever issued_at a matching prior write of this exact invite already carries,
        // rather than always stamping DateTimeOffset.UtcNow: a fresh timestamp on every retry made
        // the content differ from what a previous, successful tick already wrote, defeating
        // WriteWithRetryAsync's own read-before-write short-circuit and pushing a redundant signed
        // commit every 20 seconds until the invite expired (independent pre-PR review, cycle 4,
        // adversarial lens, medium).
        string issuedAt = current.Content is { } reused
            ? ExtractQuotedYamlValue(reused, "issued_at") ?? now.ToString("o", CultureInfo.InvariantCulture)
            : now.ToString("o", CultureInfo.InvariantCulture);
        string content = BuildYaml(
            ("node_id", candidateNodeId.ToString()),
            ("public_key", publicKeyLine),
            ("issued_at", issuedAt),
            ("invite_id", inviteId.ToString()));
        await WriteWithRetryAsync(repositoryPath, refName, path, content, $"Vouch node {candidateNodeId} (invite)", committer, signingKey, cancellationToken);
        return true;
    }

    /// <summary>Mirrors <see cref="VouchNodeAsync"/>'s own invite-id tag, idempotency, and doc
    /// comment, for <c>members/&lt;candidateOwnerFingerprint&gt;.yaml</c> instead.</summary>
    private async Task<bool> VouchMemberAsync(
        string repositoryPath, Guid inviteId, string candidateOwnerFingerprint, ProjectMemberRole role, DateTimeOffset now,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string path = $"members/{candidateOwnerFingerprint}.yaml";
        LedgerFile current = await ledger.ReadAsync(repositoryPath, MembersRefName, path, cancellationToken);
        if (current.Exists && current.Content is { } existing
            && ExtractQuotedYamlValue(existing, "invite_id") != inviteId.ToString())
        {
            return false;
        }

        string issuedAt = current.Content is { } reused
            ? ExtractQuotedYamlValue(reused, "issued_at") ?? now.ToString("o", CultureInfo.InvariantCulture)
            : now.ToString("o", CultureInfo.InvariantCulture);
        string content = BuildYaml(
            ("root_fingerprint", candidateOwnerFingerprint),
            ("role", role.Value),
            ("issued_at", issuedAt),
            ("invite_id", inviteId.ToString()));
        await WriteWithRetryAsync(repositoryPath, MembersRefName, path, content, $"Vouch member {candidateOwnerFingerprint} (invite)", committer, signingKey, cancellationToken);
        return true;
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

    private const int MaxConflictRetries = 5;

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

    /// <summary>Non-throwing wrapper around <see cref="NodeKeyStore.Fingerprint"/> for an
    /// untrusted, ledger-sourced public key line — a malformed one is simply not a candidate, never
    /// a reason to abort scanning every other node ref. Mirrors
    /// <c>GitLedgerChainReader.TryFingerprint</c>'s own shape, but also guards
    /// <see cref="FormatException"/> (invalid base64 in the key field), which that narrower catch
    /// does not.</summary>
    private static bool TryFingerprint(string publicKeyLine, out string fingerprint)
    {
        try
        {
            fingerprint = NodeKeyStore.Fingerprint(publicKeyLine);
            return true;
        }
        catch (Exception exception) when (exception is DomainValidationException or FormatException)
        {
            fingerprint = string.Empty;
            return false;
        }
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
