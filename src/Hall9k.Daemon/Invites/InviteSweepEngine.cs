using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
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
/// The minting node's own invite sweep (idea 202383dc, T2; re-verified per the 2026-09-26/27
/// security review, idea 6be68ee2): for every invite this node minted and has not yet expired, scans
/// the candidate project(s) it applies to for a node file carrying a proof, and vouches the first one
/// whose proof actually matches — HMAC(secret, that node's own key fingerprint) equal to what it
/// wrote into its own <c>invite_proof</c> field. A wrong proof never matches (there is nothing to
/// recover from being wrong — it simply never equals the real HMAC), so nothing is vouched for it,
/// and the comparison itself runs in constant time (<see cref="CryptographicOperations.FixedTimeEquals"/>)
/// rather than the ordinary short-circuiting <c>string.Equals</c>. A <c>nodes/&lt;id&gt;</c> ref only
/// ever counts as a candidate once its own <c>node.yaml</c> is confirmed signed by the
/// <c>public_key</c> that same file carries — the commit that currently produces it
/// (<c>ILedgerCommitReader.ReadSignedCommitAsync</c>'s own "commits[0] touching the path" rule, the
/// identical convention <c>GitLedgerChainReader</c> applies for a declared login) checked with
/// <c>IsSignedByAsync</c> — never the unsigned, unauthenticated content an ordinary
/// <see cref="ILedger.ReadAsync"/> would hand back: a repository collaborator with no membership of
/// their own could otherwise copy a real invitee's own proof into a node file of their own, naming
/// their own root, and be vouched in on the strength of someone else's possession. An unsigned or
/// foreign-signed candidate is skipped and logged, never treated as absent — the same "skip, never
/// abort the scan" posture a malformed field already gets. Once spent, an invite is not dropped from
/// this sweep's own outstanding set until it expires: it keeps being scanned for any OTHER candidate
/// whose proof also matches (a second holder of the identical leaked secret, racing the first inside
/// one sweep interval), and every such loser is told, once, via a node-addressed note — see
/// <see cref="NotifySpentInviteLosersAsync"/>. The vouch and slot checks below need no
/// <see cref="ILedgerChainReader"/> at all: HMAC possession of the secret is the whole proof, prior
/// to and independent of any chain trust — the vouch this sweep writes is what BEGINS that trust,
/// not something checked against it first. The successor record a node-of-owner claim also writes
/// is the one exception (idea 6be68ee2): whether THAT counts on read depends on whether this
/// sweep's own minting node is currently a live root key, so it is the one place this class reads
/// the chain at all.
/// </summary>
public sealed class InviteSweepEngine(
    IDocumentStore store, NodeContext node, ILedger ledger, ILedgerCommitReader commitReader,
    ILedgerChainReader chainReader, NodeKeyStore keyStore, ILogger<InviteSweepEngine> logger)
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

    internal sealed record CandidateNode(Guid NodeId, string KeyFingerprint, string OwnerFingerprint, string PublicKeyLine, string Proof);

    public async Task<InviteSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // No longer filters out Spent: a spent-but-unexpired invite is still scanned every tick, so
        // a second node whose own genuinely matching proof only appears after the first candidate
        // already won this invite's one use is told so, rather than seeing nothing and never
        // learning why (idea 6be68ee2: "a second node that proves a spent secret is told so"). It
        // drops out on its own once ExpiresAt <= now, the same stopping point every other invite
        // already has.
        IReadOnlyList<InviteDetails> outstanding;
        await using (IDocumentSession querySession = store.LightweightSession())
        {
            outstanding = await querySession.Query<InviteDetails>()
                .Where(invite => invite.MinterNodeId == node.NodeId && invite.ExpiresAt > now)
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

        // Mirrors projectCandidates above, for the trust chain a node-of-owner claim's own
        // successor-record gate reads (independent pre-PR review, cycle 2, adversarial lens,
        // medium): GitLedgerChainReader.ComputeAsync's own doc comment already states the "once
        // per project on every sweep tick" contract MessageSweepEngine holds to, and this cache is
        // what keeps this sweep holding to the identical contract rather than recomputing a fresh
        // chain — a full fetch/ls-remote/commit-replay against the ledger repository — for every
        // outstanding node-of-owner invite that lands a claim in the same project this tick.
        Dictionary<string, TrustChain> projectTrustChains = [];

        int spent = 0;
        foreach (InviteDetails invite in outstanding)
        {
            try
            {
                if (invite.Spent)
                {
                    await NotifySpentInviteLosersAsync(invite, projectCandidates, now, cancellationToken);
                    continue;
                }

                if (await TryClaimAsync(
                    invite, signingKey, key.Fingerprint, projectCandidates, projectTrustChains, now, cancellationToken))
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
    /// Rescans a spent-but-unexpired invite's own candidate project(s) for any node OTHER than the
    /// one that already won it whose proof still genuinely matches (idea 6be68ee2's own race: two
    /// nodes both write a valid proof inside one sweep interval, one gets vouched, the other must not
    /// be left silently unanswered) and sends each one a single node-addressed note the first time it
    /// is seen — deduplicated by this invite's own local, never-replicated
    /// <see cref="InviteLossNotified"/> record, so a still-outstanding invite never re-notifies the
    /// same loser on a later tick. Logs one warning per loser at the point the note is queued, for
    /// the minting node's own operator to see in its log. Each loser gets its own
    /// <see cref="IDocumentSession"/> (independent pre-PR review, cycle 1, adversarial lens, low): a
    /// session shared across every loser would let one loser's failed <see cref="MessageOutbox.QueueAsync"/>
    /// leave that call's own pending stream start sitting in the session, for the very next loser's
    /// unrelated save to flush alongside its own — the identical reason <see cref="TryClaimAsync"/>'s
    /// own doc comment already gives for never sharing one session across two different invites.
    /// </summary>
    private async Task NotifySpentInviteLosersAsync(
        InviteDetails invite, Dictionary<string, IReadOnlyList<CandidateNode>> projectCandidates, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        InviteAggregate? aggregate;
        IReadOnlyList<ProjectDetails> projects;
        await using (IDocumentSession readSession = store.LightweightSession())
        {
            aggregate = await readSession.Events.AggregateStreamAsync<InviteAggregate>(invite.Id, token: cancellationToken);
            if (aggregate is null || !aggregate.Spent)
            {
                return;
            }

            projects = await TargetProjectsAsync(readSession, aggregate, cancellationToken);
        }

        HashSet<Guid> notifiedThisTick = [];
        foreach (ProjectDetails project in projects)
        {
            IReadOnlyList<CandidateNode> candidates = await GetProjectCandidatesAsync(project.RepositoryPath, projectCandidates, cancellationToken);
            foreach (CandidateNode candidate in candidates)
            {
                if (candidate.NodeId == aggregate.ClaimedByNodeId || aggregate.NotifiedLosers.Contains(candidate.NodeId)
                    || notifiedThisTick.Contains(candidate.NodeId) || !ProofMatches(candidate, aggregate.Secret))
                {
                    continue;
                }

                await using IDocumentSession session = store.LightweightSession();
                try
                {
                    await MessageOutbox.QueueAsync(
                        session, node.NodeId, project.Id, aggregate.MinterOwnerFingerprint, MessageAudience.Node(candidate.NodeId),
                        about: null, MessageKind.Note,
                        $"Invite {aggregate.Id} was already claimed by another node before your own proof was seen — it is spent.",
                        now, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Best-effort, the same posture ProjectJoinCommand's own invite nudge takes:
                    // MessageOutbox.QueueAsync's own SaveChangesAsync can fail in ways this call site
                    // cannot enumerate, and a failure here simply leaves this loser un-notified for
                    // this tick, retried on the next one since InviteLossNotified below is only ever
                    // appended once the queue actually landed.
                    logger.LogWarning(
                        exception, "Could not queue the spent-invite notice for node {NodeId} on invite {InviteId}; will retry next sweep",
                        candidate.NodeId, aggregate.Id);
                    continue;
                }

                session.Events.Append(aggregate.Id, InviteDecider.NotifyLoss(aggregate, candidate.NodeId, now));
                await session.SaveChangesAsync(cancellationToken);
                notifiedThisTick.Add(candidate.NodeId);

                logger.LogWarning(
                    "Invite {InviteId} was matched by node {NodeId} in project {ProjectId} after it was already spent by node {WinnerNodeId}.",
                    aggregate.Id, candidate.NodeId, project.Id, aggregate.ClaimedByNodeId);
            }
        }
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
        InviteDetails invite, LedgerSigningKey signingKey, string myFingerprint,
        Dictionary<string, IReadOnlyList<CandidateNode>> projectCandidates, Dictionary<string, TrustChain> projectTrustChains,
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
        // already-succeeded project really is a cheap no-op: this node's own local
        // InviteProjectVouched record (never anything read back out of ledger content, which an
        // invite holder can forge — independent pre-PR review, cycle 6, adversarial lens, high)
        // says the project was already vouched into, and carries the exact issued_at that first
        // attempt used, so a retry reproduces byte-identical content and WriteWithRetryAsync's own
        // read-before-write short-circuits rather than pushing a redundant commit.
        int wroteInto = 0;
        foreach (ProjectDetails project in projects)
        {
            try
            {
                // "Already mine" is decided from this node's own local record of which projects
                // this exact invite already landed a vouch write into — never from anything read
                // back out of the ledger file itself. A file's own invite_id-shaped field is
                // ordinary content: anyone with push access to the repository can write one, and
                // an invite's own id travels in cleartext inside the secret every holder already
                // has (InviteSecret.Generate's own doc comment), so a holder of ANY invite could
                // plant a matching tag on an unrelated path and walk this node's own signing key
                // straight through what looked like a retry guard (independent pre-PR review,
                // cycle 6, adversarial lens, high). This node's own event stream is not writable by
                // an invite holder, so it is the only place "I already wrote this" can safely live.
                //
                // The record must also name WHICH candidate it was written for, not only the
                // project: FindMatch is re-run from scratch every tick against whatever node refs
                // exist right then, so a still-outstanding invite (another target project blocked
                // last tick) can start matching a different candidate on a later tick — an invite
                // holder who deletes their own node ref and forges a proof onto an already-enrolled
                // node's own ref, for instance. Treating "this project" as "already mine" regardless
                // of which candidate is asking would skip the collision guard below for a candidate
                // that never earned this slot (independent pre-PR review, cycle 1, adversarial lens,
                // medium). A mismatch simply falls through to the guard exactly as a brand-new
                // candidate would.
                bool alreadyVouched = aggregate.VouchedProjects.TryGetValue(project.Id, out VouchedProjectRecord? vouched)
                    && vouched.CandidateNodeId == candidate.NodeId
                    && string.Equals(vouched.CandidateKeyFingerprint, candidate.KeyFingerprint, StringComparison.Ordinal)
                    && string.Equals(vouched.CandidateOwnerFingerprint, candidate.OwnerFingerprint, StringComparison.Ordinal);

                // The collision guard runs — and can still refuse — before this node commits to
                // anything, exactly as it always has. What changed (independent pre-PR review,
                // cycle 7, adversarial lens, high/medium) is what happens once it passes: the slot
                // check also recognizes the candidate's own key (node case) or role (member case)
                // already sitting at that path, written by something this invite never made itself
                // — a manual h9k node vouch, the plain join genesis path, or a different invite for
                // the same candidate — as this candidate's own, not a foreign takeover, and reports
                // back the issued_at that write already used so this invite can adopt it rather than
                // refusing forever (cycle 7, adversarial lens, medium: a same-key manual vouch read
                // exactly like a hijack).
                (bool blocked, DateTimeOffset? existingIssuedAt) = aggregate.Claim == InviteClaimKind.NodeOfOwner
                    ? await NodeSlotCheckAsync(
                        project.RepositoryPath, aggregate.MinterOwnerFingerprint, candidate.NodeId,
                        candidate.PublicKeyLine, alreadyVouched, cancellationToken)
                    : await MemberSlotCheckAsync(
                        project.RepositoryPath, candidate.OwnerFingerprint, role!.Value, alreadyVouched, cancellationToken);

                if (blocked)
                {
                    if (aggregate.Claim == InviteClaimKind.NodeOfOwner)
                    {
                        // The candidate's own ref name (so its node id) is self-chosen, never
                        // verified against anything this sweep can check, so accepting it
                        // unconditionally would let an invite holder plant their own key under an
                        // already-enrolled node's own id and silently evict it — a node-of-owner
                        // invite is for a NEW node, never a route to rewrite an existing one
                        // (independent pre-PR review, cycle 4, adversarial lens, medium). The
                        // refusal fires only when the existing write carries a different key than
                        // the candidate's own — the same key is handled above, not here (cycle 7).
                        logger.LogWarning(
                            "Invite {InviteId} matched a proof claiming node id {NodeId}, which is already enrolled "
                            + "in project {ProjectId} under a different key than the candidate's own — refusing to "
                            + "overwrite an existing node; will retry next sweep.",
                            aggregate.Id, candidate.NodeId, project.Id);
                    }
                    else
                    {
                        // The candidate's own owner_fingerprint field is self-declared in its node
                        // file, never verified against anything this sweep can check — the HMAC
                        // proof binds only the candidate's key fingerprint, never that claim.
                        // Refusing to grant a different role at a fingerprint that is already a
                        // member under some other role is what stands between that unverified claim
                        // and an invite holder silently overwriting (and so demoting or escalating)
                        // an existing owner or member — a member-of-project invite is for a NEW
                        // member, never a route to rewrite an existing one (independent pre-PR
                        // review, cycle 1, adversarial lens, high). The same role for the same
                        // fingerprint is handled above, not here (cycle 7).
                        logger.LogWarning(
                            "Invite {InviteId} matched a proof claiming owner fingerprint {OwnerFingerprint}, which is "
                            + "already a member of project {ProjectId} under a different role — refusing to "
                            + "overwrite an existing member; will retry next sweep.",
                            aggregate.Id, candidate.OwnerFingerprint, project.Id);
                    }

                    continue;
                }

                DateTimeOffset issuedAt = alreadyVouched ? vouched!.VouchedAt : existingIssuedAt ?? now;

                // Committed to this node's own event stream BEFORE the ledger write it describes,
                // not after: the guard above already ruled out a foreign collision, so from here
                // this project's slot is this invite's own to fill, and recording that fact first
                // means a crash of this exact SaveChangesAsync can no longer straddle a ledger write
                // that already landed. Either neither has happened yet — retried from the top next
                // tick, guard re-run — or this local record already landed before the ledger write
                // was even attempted, closing the gap the old "commit after the write" ordering left
                // (independent pre-PR review, cycle 7, adversarial lens, high).
                if (!alreadyVouched)
                {
                    session.Events.Append(
                        aggregate.Id,
                        InviteDecider.VouchProject(
                            aggregate, project.Id, issuedAt, candidate.NodeId, candidate.KeyFingerprint, candidate.OwnerFingerprint));
                    await session.SaveChangesAsync(cancellationToken);
                }

                if (aggregate.Claim == InviteClaimKind.NodeOfOwner)
                {
                    await WriteNodeVouchAsync(
                        project.RepositoryPath, aggregate.MinterOwnerFingerprint, candidate.NodeId,
                        candidate.PublicKeyLine, candidate.KeyFingerprint, issuedAt, committer, signingKey, cancellationToken);

                    // Only when this sweep's own minting node key is itself currently a live root
                    // key of the owner it minted for (idea 6be68ee2): a successor record only ever
                    // counts on read when it is signed by one of the root's own live keys, so an
                    // ordinary fleet node's own invite has nothing to write here — writing it anyway
                    // landed a permanent, unresolvable "unverifiable successor" line in h9k status
                    // for every node-of-owner invite an ordinary node ever minted (independent pre-PR
                    // review, cycle 1, both lenses, medium — the identical gate NodeVouchCommand's own
                    // vouch flow already applies). Best-effort — the vouch itself already landed
                    // above — so a failure here is logged and swallowed rather than blocking this
                    // invite's own spend.
                    TrustChain chain = await GetProjectTrustChainAsync(project.RepositoryPath, projectTrustChains, cancellationToken);
                    if (chain.OwnerChains.TryGetValue(aggregate.MinterOwnerFingerprint, out TrustedOwner? mintingOwner)
                        && mintingOwner.IsLiveRootKey(myFingerprint))
                    {
                        try
                        {
                            await SuccessionLedgerWriter.WriteSuccessorAsync(
                                ledger, project.RepositoryPath, aggregate.MinterOwnerFingerprint, candidate.NodeId, candidate.PublicKeyLine,
                                issuedAt, committer, signingKey, cancellationToken);
                        }
                        catch (Exception exception)
                            when (exception is LedgerPushRejectedException or InvalidOperationException or DomainConflictException)
                        {
                            logger.LogWarning(
                                exception, "Invite {InviteId} vouched node {NodeId} but could not also write its successor "
                                + "record in project {ProjectId}; will retry next sweep", aggregate.Id, candidate.NodeId, project.Id);
                        }
                    }

                    await InviteSpendLedgerWriter.WriteAsync(
                        ledger, project.RepositoryPath, aggregate.MinterOwnerFingerprint, aggregate.Id, aggregate.SecretHash,
                        aggregate.Claim, aggregate.Role, aggregate.ExpiresAt, committer, signingKey, cancellationToken);
                    wroteInto++;
                }
                else
                {
                    // idea 6be68ee2, companion 1bb803e1: members/<fingerprint>.yaml is the one write
                    // 1bb803e1 restricts to a live root key. When this sweep's own minting node key
                    // already is one for the minting owner, write it directly - the identical flow this
                    // branch always ran. When it is not, this node's own key was never the write's to
                    // make in the first place; request it from whichever node holds the owner's own
                    // highest-ranked live root key instead, and leave this project's own slot open
                    // (wroteInto never increments here) until that node's own outcome reply says done -
                    // reacted to by OwnerActRequestWatchLoop, never by this sweep.
                    //
                    // myFingerprint == aggregate.MinterOwnerFingerprint is checked first, ahead of any
                    // chain read: a fingerprint is a hash of a specific public key, so this node can
                    // only ever produce this owner's own root identity string here by holding that exact
                    // root's own private key - the genesis-root case needs no ledger confirmation at
                    // all. This also carries the sweep through a project whose own ledger has not yet
                    // recorded this owner's chain at all (this project's own ledger predates it, or the
                    // owner's root has genuinely never pushed an owners/<root> ref), where
                    // GetProjectTrustChainAsync's own OwnerChains has no entry to consult either way -
                    // the identical shape this branch always tolerated before this task ever read the
                    // chain here at all. A key rotated in by succession (idea 6be68ee2) never equals
                    // this string (RootFingerprint names K0's own identity, never a rotation's), so that
                    // case still falls through to the chain's own RootKeys check below.
                    TrustChain chain = await GetProjectTrustChainAsync(project.RepositoryPath, projectTrustChains, cancellationToken);
                    chain.OwnerChains.TryGetValue(aggregate.MinterOwnerFingerprint, out TrustedOwner? mintingOwner);
                    bool iAmLiveRootKey = myFingerprint == aggregate.MinterOwnerFingerprint
                        || (mintingOwner is not null && mintingOwner.IsLiveRootKey(myFingerprint));

                    if (iAmLiveRootKey)
                    {
                        await MemberVouchLedgerWriter.WriteAsync(
                            ledger, project.RepositoryPath, candidate.OwnerFingerprint, role!.Value, issuedAt, committer,
                            signingKey, cancellationToken);

                        // Appended only once the ledger write it describes has actually landed, unlike
                        // InviteProjectVouched above: that record is this sweep's own internal retry
                        // guard and is deliberately written before the ledger write is even attempted, but
                        // this event feeds ProjectDetails.Members/ProjectAggregate.Members, a read model
                        // of what the ledger actually holds. Recording it before the write that grounds it
                        // would leave a member listed here forever if every retry of that write then failed
                        // until the invite's own expiry dropped it from the outstanding query — guessing at
                        // an unobserved fact (independent pre-PR review, cycle 1, conformance lens, low).
                        //
                        // Gated on !alreadyVouched, the same as InviteProjectVouched above: without it, a
                        // tick that lands this write (MemberVouchLedgerWriter is retry-idempotent and
                        // no-ops once the ledger already holds the target content) but then fails the
                        // later MarkInviteSpentInLedgerAsync call left the invite outstanding with
                        // alreadyVouched now true, so every following tick re-appended a fresh
                        // MemberVouched stamped with that tick's own now — an unbounded run of duplicate
                        // events, each carrying a fabricated issued_at that never matched when membership
                        // actually took effect (independent pre-PR review, cycle 2, adversarial lens,
                        // medium).
                        if (!alreadyVouched)
                        {
                            session.Events.Append(
                                project.Id, ProjectDecider.VouchMember(project.Id, candidate.OwnerFingerprint, role!.Value, now));
                            await session.SaveChangesAsync(cancellationToken);
                        }

                        await InviteSpendLedgerWriter.WriteAsync(
                            ledger, project.RepositoryPath, aggregate.MinterOwnerFingerprint, aggregate.Id, aggregate.SecretHash,
                            aggregate.Claim, aggregate.Role, aggregate.ExpiresAt, committer, signingKey, cancellationToken);
                        wroteInto++;
                    }
                    else if (mintingOwner is null
                        || !await RequestMemberWriteAsync(
                            session, aggregate, project, mintingOwner, candidate, role!.Value, issuedAt, now, cancellationToken))
                    {
                        logger.LogWarning(
                            "Invite {InviteId} could not resolve a node holding a live root key for owner "
                            + "{Root} in project {ProjectId} to request its member write from; will retry next sweep.",
                            aggregate.Id, aggregate.MinterOwnerFingerprint, project.Id);
                    }
                }
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
    /// Every node ref in <paramref name="repositoryPath"/> that actually carries a usable, SIGNED
    /// candidate — a well-formed <c>invite_proof</c>, <c>public_key</c>, and <c>owner_fingerprint</c>,
    /// AND the commit that currently produces that <c>node.yaml</c> signed by the <c>public_key</c>
    /// that same file carries (<see cref="TryResolveCandidateAsync"/>) — scanned at most once per
    /// sweep tick regardless of how many outstanding invites target this project (the cache in
    /// <see cref="SweepOnceAsync"/>), and skipping the file fetch for any node ref whose tip
    /// <see cref="_lastKnownRefs"/> already saw unchanged, reusing that ref's own previously-resolved
    /// candidate (or lack of one) instead: that ref's content, and so whatever proof it does or does
    /// not carry, cannot have changed either (independent pre-PR review, cycle 1, conformance and
    /// adversarial lenses, both medium). Reusing the cached candidate rather than treating an unmoved
    /// tip as "nothing here" is what keeps a candidate whose earlier vouch/spend write failed partway
    /// through still offered to every later tick — its node ref's tip never moves again after the
    /// join, so a naive skip would otherwise drop it forever (independent pre-PR review, cycle 2,
    /// adversarial lens, high). An unsigned or foreign-signed candidate caches exactly like a
    /// malformed field does — <c>null</c>, keyed by the unmoved tip — so it is never re-verified on
    /// every later tick for as long as that ref's own content stays exactly what it was.
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
            CandidateNode? candidate = await TryResolveCandidateAsync(
                commitReader, repositoryPath, nodeRef.RefName, path, candidateNodeId, logger, cancellationToken);
            _lastKnownRefs[tipKey] = (nodeRef.Sha, candidate);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        cache[repositoryPath] = candidates;
        return candidates;
    }

    /// <summary>This tick's own trust chain for <paramref name="repositoryPath"/>, computed at most
    /// once regardless of how many outstanding node-of-owner invites land a claim in this project
    /// this tick — the same "once per project on every sweep tick" contract
    /// <see cref="GitLedgerChainReader.ComputeAsync"/>'s own doc comment already states for
    /// <c>MessageSweepEngine</c> (independent pre-PR review, cycle 2, adversarial lens, medium: an
    /// uncached call here reintroduced the unbounded per-claim ledger fetch <c>StatusCommand</c>'s
    /// own fix in this same cycle removed, just at a different call site).</summary>
    private async Task<TrustChain> GetProjectTrustChainAsync(
        string repositoryPath, Dictionary<string, TrustChain> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(repositoryPath, out TrustChain? cached))
        {
            return cached;
        }

        TrustChain chain = await chainReader.ComputeAsync(repositoryPath, cancellationToken);
        cache[repositoryPath] = chain;
        return chain;
    }

    /// <summary>
    /// One node ref's own candidate resolution, pulled out of <see cref="GetProjectCandidatesAsync"/>
    /// as a self-contained, <see cref="IDocumentStore"/>-free static so a pure unit test can drive it
    /// directly against a <c>FakeLedgerCommitReader</c> (Brian's 2026-09-13 testing rule) — the
    /// adversarial shape this exists to catch: a repository collaborator with no membership of their
    /// own copies a real invitee's own proof into a node file naming their own <c>public_key</c>
    /// (never the victim's, which only the victim's own private key can sign a commit for) and their
    /// own root, hoping the sweep vouches it purely on the strength of the copied HMAC. <c>null</c>
    /// for a path that does not exist, a field that does not parse, or — the gate this task adds — a
    /// commit that is not signed by the exact <c>public_key</c> the file itself carries; every case
    /// logs a warning except plain absence, which is the everyday steady state for a node ref that
    /// simply has not announced itself yet.
    /// </summary>
    internal static async Task<CandidateNode?> TryResolveCandidateAsync(
        ILedgerCommitReader commitReader, string repositoryPath, string refName, string path, Guid candidateNodeId,
        ILogger logger, CancellationToken cancellationToken)
    {
        LedgerSignedCommit? signedCommit = await commitReader.ReadSignedCommitAsync(repositoryPath, refName, path, cancellationToken);
        if (signedCommit is null)
        {
            return null;
        }

        string content = signedCommit.Content;
        string? proof = ExtractQuotedYamlValue(content, "invite_proof");
        string? publicKeyLine = ExtractQuotedYamlValue(content, "public_key");
        string? ownerFingerprint = ExtractQuotedYamlValue(content, "owner_fingerprint");
        if (proof.IsBlank() || publicKeyLine.IsBlank() || ownerFingerprint.IsBlank())
        {
            return null;
        }

        // Both guards turn an untrusted, ledger-sourced field into "not a candidate" rather
        // than a thrown exception or a blindly-trusted value: a malformed public key line must
        // never abort the scan for every other node ref behind it (independent pre-PR review,
        // cycle 1, conformance lens, medium — anyone with push on the repository can otherwise
        // poison every sweep of this invite until it expires), and a malformed owner fingerprint
        // must never be handed to a ledger path unvalidated (adversarial lens, high).
        if (!TryFingerprint(publicKeyLine, out string candidateFingerprint) || !NodeKeyStore.IsFingerprint(ownerFingerprint))
        {
            return null;
        }

        // The signature gate itself (2026-09-26/27 security review, idea 6be68ee2, finding 3): the
        // commit that currently produces this exact node.yaml must be signed by the public_key that
        // same file carries — never merely by whoever pushed it. A candidate whose own public_key is
        // the victim's (to make the copied proof match) but whose commit is signed by the attacker's
        // own key fails this, since only the victim's own private key could ever sign a commit
        // allowed-signers-checked against the victim's own public_key line.
        if (!await commitReader.IsSignedByAsync(repositoryPath, signedCommit.RawCommitBytes, publicKeyLine, cancellationToken))
        {
            logger.LogWarning(
                "Invite sweep candidate {RefName} skipped: {Path} is not signed by its own public_key.", refName, path);
            return null;
        }

        return new CandidateNode(candidateNodeId, candidateFingerprint, ownerFingerprint, publicKeyLine, proof);
    }

    private static CandidateNode? FindMatch(IReadOnlyList<CandidateNode> candidates, string secret)
    {
        foreach (CandidateNode candidate in candidates)
        {
            if (ProofMatches(candidate, secret))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="candidate"/>'s own <c>invite_proof</c> field is the real
    /// HMAC for <paramref name="secret"/> and this candidate's own key fingerprint — compared in
    /// constant time (<see cref="CryptographicOperations.FixedTimeEquals"/>) rather than the ordinary
    /// short-circuiting <c>string.Equals</c>, since <see cref="CandidateNode.Proof"/> is untrusted,
    /// ledger-sourced content an adversary controls byte-for-byte.</summary>
    internal static bool ProofMatches(CandidateNode candidate, string secret) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate.Proof), Encoding.UTF8.GetBytes(InviteSecret.ComputeProof(secret, candidate.KeyFingerprint)));

    /// <summary>
    /// Decides whether this invite may claim
    /// <c>owners/&lt;rootFingerprint&gt;/nodes/&lt;candidateNodeId&gt;.yaml</c>, without writing
    /// anything. <c>Blocked</c> only when a file already lives at that path, this exact invite
    /// never recorded writing it (<paramref name="alreadyVouched"/> is <c>false</c>), and the
    /// existing file's own <c>public_key</c> differs from the candidate's own — an already-enrolled
    /// DIFFERENT node under this node id, not this candidate re-appearing under something else's
    /// write. <paramref name="alreadyVouched"/> comes from this node's own event stream
    /// (<see cref="InviteAggregate.VouchedProjects"/>), never from ledger content: a bare existence
    /// check alone would refuse this invite's own successful prior write forever on retry
    /// (independent pre-PR review, cycle 4, both lenses, high/medium), and a self-declared tag
    /// inside the file content itself is attacker-writable and so cannot be trusted to make that
    /// call either (independent pre-PR review, cycle 6, adversarial lens, high). A same-key existing
    /// file — this exact candidate, vouched by something this invite never wrote itself (a direct
    /// <c>h9k node vouch</c>, the plain join genesis path, or a different invite) — is not a
    /// collision either: it is reported back as <c>ExistingIssuedAt</c> so the caller adopts that
    /// write's own timestamp instead of refusing forever (independent pre-PR review, cycle 7,
    /// adversarial lens, medium: a same-key manual vouch previously read exactly like a hijack).
    /// </summary>
    private async Task<(bool Blocked, DateTimeOffset? ExistingIssuedAt)> NodeSlotCheckAsync(
        string repositoryPath, string rootFingerprint, Guid candidateNodeId, string publicKeyLine, bool alreadyVouched,
        CancellationToken cancellationToken)
    {
        if (alreadyVouched)
        {
            return (false, null);
        }

        string refName = $"refs/hall9k/ledger/owners/{rootFingerprint}";
        string path = $"owners/{rootFingerprint}/nodes/{candidateNodeId}.yaml";
        LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (!current.Exists)
        {
            return (false, null);
        }

        string content = current.Content ?? string.Empty;
        string? existingPublicKey = ExtractQuotedYamlValue(content, "public_key");
        if (!string.Equals(existingPublicKey, publicKeyLine, StringComparison.Ordinal))
        {
            return (true, null);
        }

        return (false, ParseIssuedAt(content));
    }

    /// <summary>Writes (or, on a retry of this exact invite's own earlier attempt — or a same-key
    /// write already made by something else — cheaply confirms)
    /// <c>owners/&lt;rootFingerprint&gt;/nodes/&lt;candidateNodeId&gt;.yaml</c>. Only ever called
    /// after <see cref="NodeSlotCheckAsync"/> has already ruled out a foreign collision; issuedAt is
    /// this invite's own locally-recorded first-attempt timestamp on a retry, or that same-key
    /// write's own recorded timestamp, never a fresh <see cref="DateTimeOffset.UtcNow"/> — so the
    /// content this call builds is byte-identical to what is already there, and
    /// <see cref="WriteWithRetryAsync"/>'s own read-before-write short-circuit skips pushing a
    /// redundant signed commit every 20 seconds until the invite expires (independent pre-PR
    /// review, cycle 4, adversarial lens, medium).</summary>
    private async Task WriteNodeVouchAsync(
        string repositoryPath, string rootFingerprint, Guid candidateNodeId, string publicKeyLine, string keyFingerprint,
        DateTimeOffset issuedAt, LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{rootFingerprint}";
        string path = $"owners/{rootFingerprint}/nodes/{candidateNodeId}.yaml";
        string content = BuildYaml(
            ("node_id", candidateNodeId.ToString()),
            ("public_key", publicKeyLine),
            ("issued_at", issuedAt.ToString("o", CultureInfo.InvariantCulture)));
        await WriteWithRetryAsync(
            repositoryPath, refName, path, content, $"Vouch node {candidateNodeId} key {keyFingerprint} (invite)", committer, signingKey,
            cancellationToken);
    }

    /// <summary>Mirrors <see cref="NodeSlotCheckAsync"/>'s own guard, for
    /// <c>members/&lt;candidateOwnerFingerprint&gt;.yaml</c> instead — the path is already keyed by
    /// the candidate's own fingerprint, so the field that stands in for "this candidate's own prior
    /// write" is <c>role</c> rather than a public key: the same role already granted to this exact
    /// fingerprint by something this invite never wrote itself is this candidate's own, not a
    /// foreign takeover (independent pre-PR review, cycle 7, adversarial lens, medium).</summary>
    private async Task<(bool Blocked, DateTimeOffset? ExistingIssuedAt)> MemberSlotCheckAsync(
        string repositoryPath, string candidateOwnerFingerprint, ProjectMemberRole role, bool alreadyVouched,
        CancellationToken cancellationToken)
    {
        if (alreadyVouched)
        {
            return (false, null);
        }

        string path = $"members/{candidateOwnerFingerprint}.yaml";
        LedgerFile current = await ledger.ReadAsync(repositoryPath, MembersRefName, path, cancellationToken);
        if (!current.Exists)
        {
            return (false, null);
        }

        string content = current.Content ?? string.Empty;
        string? existingRole = ExtractQuotedYamlValue(content, "role");
        if (!string.Equals(existingRole, role.Value, StringComparison.Ordinal))
        {
            return (true, null);
        }

        return (false, ParseIssuedAt(content));
    }

    /// <summary>
    /// Sends this invite's own member write to whichever node holds
    /// <paramref name="mintingOwner"/>'s own highest-ranked live root key
    /// (<see cref="TrustedOwner.ResolveRootActingNodeId"/>), unless one is already outstanding — this
    /// node's own outbox already carries an unhandled-by-nothing-yet <see cref="MessageKind.OwnerActRequest"/>
    /// for this exact invite, so a tick that finds one queued already leaves it to
    /// <see cref="MessageOutbox"/>'s own ordinary retry-until-flushed delivery and
    /// <c>OwnerActRequestWatchLoop</c>'s own reaction to the eventual reply, rather than queuing a
    /// second, redundant envelope every five seconds until the root answers. Returns false only when
    /// no node resolves at all — the caller's own cue to log and retry next sweep; true whether this
    /// call just queued the request or found one already in flight.
    /// </summary>
    private async Task<bool> RequestMemberWriteAsync(
        IDocumentSession session, InviteAggregate aggregate, ProjectDetails project, TrustedOwner mintingOwner,
        CandidateNode candidate, ProjectMemberRole role, DateTimeOffset issuedAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (mintingOwner.ResolveRootActingNodeId() is not { } targetNodeId)
        {
            return false;
        }

        string about = aggregate.Id.ToString();
        string requestKind = MessageKind.OwnerActRequest.Value;
        bool alreadyRequested = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == node.NodeId && message.Kind == requestKind && message.About == about)
            .AnyAsync(cancellationToken);
        if (alreadyRequested)
        {
            return true;
        }

        OwnerActEnvelopeCodec.OwnerActRequestRecord record = new(
            aggregate.Id, node.NodeId, candidate.OwnerFingerprint, role, issuedAt);
        await MessageOutbox.QueueAsync(
            session, node.NodeId, project.Id, aggregate.MinterOwnerFingerprint, MessageAudience.Node(targetNodeId),
            about, MessageKind.OwnerActRequest, OwnerActEnvelopeCodec.Encode(record), now, cancellationToken);
        return true;
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

    /// <summary>The <c>issued_at</c> an existing node or member file already carries, so a
    /// same-key/same-role slot recognized as this candidate's own
    /// (<see cref="NodeSlotCheckAsync"/>/<see cref="MemberSlotCheckAsync"/>) can be adopted with the
    /// timestamp that write already used rather than a fresh one — keeping the eventual re-write
    /// byte-identical to what is already there. A missing or malformed value falls back to
    /// <c>null</c>, which the caller treats the same as no existing file at all: worst case, one
    /// harmless extra commit refreshes it.</summary>
    private static DateTimeOffset? ParseIssuedAt(string content)
    {
        string? raw = ExtractQuotedYamlValue(content, "issued_at");
        return raw is not null
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                ? parsed
                : null;
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
