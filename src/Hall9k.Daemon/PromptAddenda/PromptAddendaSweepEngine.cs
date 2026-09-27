using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx.Events;
using Marten;

namespace Hall9k.Daemon.PromptAddenda;

/// <summary>How many ledger writes/deletes and local materializations one sweep tick actually made,
/// for the loop's own log line.</summary>
public sealed record PromptAddendaSweepResult(int Pushed, int Materialized);

/// <summary>
/// The only place a project's own prompt-addendum ledger file is ever written (idea b9b09779, piece
/// 6): a dispatched agent session runs <c>h9k project prompt-addendum set/remove</c>, which appends
/// only a domain event — never touches <see cref="ILedger"/> itself — and this sweep is what turns
/// that event into <c>prompt-addenda/&lt;builder&gt;.md</c> on
/// <see cref="LedgerRefRegistry.PromptAddenda"/>, the same "CLI records the fact, the daemon alone
/// writes the ledger" split <c>InviteSweepEngine</c> already uses for a vouch.
/// <para>
/// Two independent jobs per eligible project, every tick: <see cref="PushAsync"/> scans this node's
/// own new <see cref="ProjectPromptAddendumSet"/>/<see cref="ProjectPromptAddendumRemoved"/> events
/// (never re-reading one already scanned, via <see cref="PromptAddendaSyncPosition"/>) and pushes
/// each to the ledger; <see cref="MaterializeAsync"/> reads the ledger's own current content for
/// EVERY builder key, regardless of who wrote it, and keeps this node's local disk copy — what
/// every prompt builder's loader actually reads — in step. The two are separate because a fellow
/// member's own addendum never appears in this node's own event log until the distributed-team
/// chain replicates it, but the ledger, fetched fresh here, already reaches every member's node
/// today: only the materialize half is what makes a teammate's addendum work before that chain
/// ships.
/// </para>
/// </summary>
public sealed class PromptAddendaSweepEngine(
    IDocumentStore store, NodeContext node, ILedger ledger, NodeKeyStore keyStore,
    ILogger<PromptAddendaSweepEngine> logger, ILedgerChainReader chainReader, ILedgerCommitReader commitReader)
{
    private const int MaxConflictRetries = 5;

    /// <summary>Each project's own prompt-addenda ref tip as of this node's last look — so a tick
    /// that finds an unmoved tip skips the fetch <see cref="ILedger.ReadAllAsync"/> would otherwise
    /// cost, the same tip-gating <c>InviteSweepEngine._lastKnownRefs</c> already uses for exactly
    /// this reason. In-memory and per-process by design: a restart just re-reads once, cheap and
    /// correct, never lossy (independent pre-PR review, cycle 1, conformance and adversarial
    /// lenses, both medium: every tick otherwise fetched all four builder paths individually, once
    /// per project, forever, even for a project that has never had an addendum).
    /// <para>
    /// Keyed on the project's own repository path AND home together, not repository path alone: the
    /// tip only ever describes whether the LEDGER content moved, but what this cache actually gates
    /// is whether the LOCAL disk copy under that home still matches it, and a repository re-pointed
    /// to a different home (<c>h9k project set --home</c>, <c>h9k project init</c> repairing a wiped
    /// one) starts that copy back at empty without moving the ledger tip at all. Keying on the
    /// repository path alone left a re-homed project's own materialize permanently skipped — the
    /// unmoved tip kept matching the cached one forever, so the new home's <c>prompt-addenda/</c>
    /// stayed empty until something else (a remove, a set) moved the tip again (independent pre-PR
    /// review, cycle 1, conformance lens, medium).
    /// </para>
    /// <para>
    /// Paired with a signature over every currently Owner-role member's own root and vouched-node
    /// keys (<see cref="ComputeOwnerRoleKeySetSignature"/>), not the ledger tip alone (idea 6be68ee2,
    /// trust-ledger finding 6): the owner test below depends on the live chain, not merely on the
    /// ledger's own content, so a node revoked or an owner demoted between two ticks must still
    /// re-materialize even though nothing pushed to the prompt-addenda ref itself moved.
    /// </para>
    /// </summary>
    private readonly Dictionary<(string RepositoryPath, string Home), (string? Tip, string OwnerRoleKeySet)> _lastKnownPromptAddendaTips = [];

    /// <summary>Per-project backoff for a failing <see cref="ILedger.ListRefsAsync"/> call — doubled
    /// on every consecutive failure up to <see cref="ListRefsBackoffCeiling"/>, and forgotten the
    /// moment a call succeeds again. Without it, a project whose remote is unreachable (offline, no
    /// SSH agent loaded, a repository registered with no remote at all) paid for a network round
    /// trip and logged a warning on every single tick forever, since <see cref="SweepOnceAsync"/>
    /// filters projects only on not-archived and a non-blank repository path, with no notion of
    /// "this project's ledger is currently unreachable" the way <c>MessageSweepEngine</c>'s own
    /// identity gate or <c>InviteSweepEngine</c>'s own outstanding-invite gate would have caught
    /// (independent pre-PR review, cycle 1, adversarial lens, medium). Scoped per project, not
    /// loop-wide like <c>PullRequestMonitor.ApplyBackoff</c>: one project's own unreachable remote
    /// must never slow down the ledger poll for every other, healthy project this node also
    /// materializes addenda for.</summary>
    private readonly Dictionary<Guid, (DateTimeOffset RetryAfter, TimeSpan Interval)> _listRefsBackoff = [];

    private static readonly TimeSpan ListRefsBackoffFloor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListRefsBackoffCeiling = TimeSpan.FromMinutes(30);

    /// <summary>Shared by every call site that pays for a network round trip against this
    /// project's own remote (the trust-chain read in <see cref="SweepOnceAsync"/> and the
    /// <see cref="ILedger.ListRefsAsync"/> call in <see cref="MaterializeAsync"/> alike): both are a
    /// bare <c>git ls-remote</c> against the identical remote, so a failure on either one is one
    /// remote being unreachable, not two independent facts, and both back off together.</summary>
    private bool IsBackingOffListRefs(Guid projectId, DateTimeOffset now) =>
        _listRefsBackoff.TryGetValue(projectId, out (DateTimeOffset RetryAfter, TimeSpan Interval) backoff)
        && now < backoff.RetryAfter;

    private void RecordListRefsFailure(Guid projectId, DateTimeOffset now)
    {
        _listRefsBackoff.TryGetValue(projectId, out (DateTimeOffset RetryAfter, TimeSpan Interval) backoff);
        TimeSpan nextInterval = backoff.Interval == default
            ? ListRefsBackoffFloor
            : TimeSpan.FromTicks(Math.Min(backoff.Interval.Ticks * 2, ListRefsBackoffCeiling.Ticks));
        _listRefsBackoff[projectId] = (now + nextInterval, nextInterval);
    }

    private void ClearListRefsBackoff(Guid projectId) => _listRefsBackoff.Remove(projectId);

    public async Task<PromptAddendaSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> projects;
        await using (IDocumentSession lookup = store.LightweightSession())
        {
            projects = await lookup.Query<ProjectDetails>()
                .Where(project => !project.IsArchived)
                .ToListAsync(cancellationToken);
        }

        int pushed = 0;
        int materialized = 0;
        foreach (ProjectDetails project in projects.Where(project => project.RepositoryPath.IsNotBlank()))
        {
            // Computed once per project, up front, and handed to both halves below: PushAsync's own
            // new owner-role gate and MaterializeAsync's own owner test both need the identical live
            // chain, and a read that fails must fail closed for both — never push under a stale
            // notion of who is Owner-role, and never materialize over the last good files
            // (MessageSweepEngine.SweepOnceAsync's own per-project "skip this tick, retry next
            // sweep" pattern, idea 6be68ee2, trust-ledger finding 6).
            // Gated behind the identical per-project backoff MaterializeAsync's own ListRefsAsync
            // call already earns on failure (both are a bare `git ls-remote origin <prefix>*`
            // against the same remote), not merely skipped once and retried next tick: without
            // this, a project whose remote is unreachable paid for this chain read's own network
            // round trip and warning every 30-second tick forever, exactly the cost the backoff
            // field was introduced to remove from ListRefsAsync (independent pre-PR review, cycle
            // 1, conformance lens, medium).
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (IsBackingOffListRefs(project.Id, now))
            {
                // Recorded on every backed-off tick, not merely the one that first hit the
                // failure: before PushAsync's own owner-role gate needed a chain read at all, an
                // unreachable remote surfaced here by failing WriteAsync/DeleteAsync directly, and
                // that failure stayed recorded — and so kept showing up in `list`/`show`'s own
                // "daemon has not been able to push" warning — for the whole outage. Skipping this
                // tick via `continue`, with no call to RecordPushFailureAsync, silently dropped
                // that warning the moment the very next tick's own backoff kicked in, even though
                // the push itself was still just as blocked (independent pre-PR review, cycle 3,
                // conformance lens, medium).
                await RecordPushFailureAsync(
                    project.Id,
                    "Prompt-addenda push is skipped while this project's own ledger remote is backed off after "
                    + "a recent failure.",
                    cancellationToken);
                continue;
            }

            TrustChain trustChain;
            try
            {
                trustChain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
            }
            catch (Exception exception)
            {
                RecordListRefsFailure(project.Id, now);
                logger.LogWarning(
                    exception, "Prompt-addenda trust chain read failed for project {ProjectId}; push and "
                    + "materialize are both skipped this tick and retried next sweep", project.Id);
                await RecordPushFailureAsync(project.Id, exception.Message, cancellationToken);
                continue;
            }

            ClearListRefsBackoff(project.Id);

            try
            {
                int pushedForProject = await PushAsync(project, trustChain, cancellationToken);
                pushed += pushedForProject;

                // Cleared on every tick that reaches here at all, not only one that actually
                // pushed something: PushAsync completing without throwing means nothing addendum-
                // shaped currently fails to push, whether or not this tick had anything addendum-
                // shaped to push in the first place. Gating this on pushedForProject > 0 left a
                // failure recorded by something else entirely — an ordinary SaveChangesAsync
                // hiccup on a tick with no addendum candidates at all — stuck warning forever,
                // since a tick with nothing to push can never clear it (independent pre-PR
                // review, cycle 1, conformance lens, low).
                await ClearPushFailureAsync(project.Id, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Prompt-addenda push failed for project {ProjectId}; will retry next sweep", project.Id);
                await RecordPushFailureAsync(project.Id, exception.Message, cancellationToken);
            }

            try
            {
                materialized += await MaterializeAsync(project, trustChain, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Prompt-addenda materialize failed for project {ProjectId}; will retry next sweep",
                    project.Id);
            }
        }

        return new PromptAddendaSweepResult(pushed, materialized);
    }

    /// <summary>
    /// Records what a push attempt just threw against this project's own sync position, on a fresh
    /// session — the one <see cref="PushAsync"/> itself was using may have already failed its own
    /// <see cref="IDocumentSession.SaveChangesAsync"/> call, or never reached one, so a new session
    /// is the only one guaranteed to still be usable here. Best-effort: a failure recording the
    /// failure is logged and swallowed rather than allowed to mask the original one.
    /// </summary>
    private async Task RecordPushFailureAsync(Guid projectId, string error, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            PromptAddendaSyncPosition position =
                await session.LoadAsync<PromptAddendaSyncPosition>(projectId, cancellationToken)
                ?? new PromptAddendaSyncPosition { Id = projectId };
            position.LastPushError = error;
            position.LastPushErrorAt = DateTimeOffset.UtcNow;
            session.Store(position);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Could not record the prompt-addenda push failure itself for project {ProjectId}", projectId);
        }
    }

    /// <summary>The success-path mirror of <see cref="RecordPushFailureAsync"/>: called on every
    /// tick whose own <see cref="PushAsync"/> completed without throwing, whether or not it pushed
    /// anything (<see cref="SweepOnceAsync"/>'s own doc on why). Nothing to do when this project
    /// never had a recorded failure, so the ordinary, always-succeeding case still pays for the
    /// document load but never the store and save it does not need.</summary>
    private async Task ClearPushFailureAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        PromptAddendaSyncPosition? position = await session.LoadAsync<PromptAddendaSyncPosition>(projectId, cancellationToken);
        if (position is not { LastPushError: not null })
        {
            return;
        }

        position.LastPushError = null;
        position.LastPushErrorAt = null;
        session.Store(position);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Scans this project's own event log, past wherever the last sweep left off, for a prompt-
    /// addendum change this node's own stream carries, and pushes each one to the ledger in order.
    /// <para>
    /// Skips entirely — before touching this project's own sync position at all — when this node's
    /// own owner is not currently an Owner-role member of <paramref name="project"/> (idea 6be68ee2,
    /// trust-ledger finding 6): the ledger holds exactly one file per builder for the whole project
    /// (<see cref="LedgerRefRegistry.PromptAddenda"/>, no owner segment), so a member's own push
    /// would be refused everywhere it is read back — including this same node's own next
    /// <see cref="MaterializeAsync"/> — the moment <c>MaterializeAsync</c>'s own owner test ships.
    /// Owner-only is the honest rule rather than a wasted, silently-inert push. Left untouched on
    /// skip (never advanced) so a later promotion picks up exactly where this node left off, with
    /// nothing lost in between.
    /// </para>
    /// </summary>
    private async Task<int> PushAsync(ProjectDetails project, TrustChain trustChain, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, project.OwnerId, cancellationToken);
        if (ownerRootFingerprint is null || trustChain.RoleOf(ownerRootFingerprint) != MembershipRole.Owner)
        {
            return 0;
        }

        PromptAddendaSyncPosition? position =
            await session.LoadAsync<PromptAddendaSyncPosition>(project.Id, cancellationToken);
        long sinceSequence = position?.LastScannedGlobalSequence ?? 0;

        IReadOnlyList<IEvent> candidates = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamId == project.Id && e.Sequence > sinceSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // A replicated change can land on this stream behind a newer one for the same builder key (a
        // catch-up answer serves a pre-switch-on head after the tail), so a change stamped older than
        // the one the projection applied for its key is never pushed: it would overwrite the newer
        // file in the ledger. The projection is read after the events, so it has applied every event
        // scanned above.
        ProjectDetails? applied = await session.LoadAsync<ProjectDetails>(project.Id, cancellationToken);
        bool Superseded(string builderKey, DateTimeOffset stamp) =>
            applied is not null && applied.PromptAddendumStamps.TryGetValue(builderKey, out DateTimeOffset newest) && stamp < newest;

        // A teammate's own addendum lands on this identical stream through EventReplicationInbox,
        // which stamps ReceivedFromNodeId on every record it merges — never something the sender
        // claims, so it cannot be forged by whatever the event's own payload says. Never pushed from
        // here: doing so would re-sign a teammate's content under this node's own committer and
        // signing key, as if this install had authored it (idea 6be68ee2, trust-ledger finding 6).
        bool ReceivedFromPeer(IEvent candidateEvent) =>
            candidateEvent.GetHeader(ReplicationEventHeaders.ReceivedFromNodeId) is not null;

        int pushed = 0;
        LedgerCommitter? committer = null;
        LedgerSigningKey? signingKey = null;

        foreach (IEvent candidate in candidates)
        {
            switch (candidate.Data)
            {
                case ProjectPromptAddendumSet set when Superseded(set.BuilderKey, set.SetAt) || ReceivedFromPeer(candidate):
                case ProjectPromptAddendumRemoved removed
                    when Superseded(removed.BuilderKey, removed.RemovedAt) || ReceivedFromPeer(candidate):
                    break;
                case ProjectPromptAddendumSet set:
                    (committer, signingKey) = await EnsureIdentityAsync(session, project, committer, signingKey, cancellationToken);
                    await WriteAsync(project.RepositoryPath, set, committer, signingKey, trustChain, cancellationToken);
                    pushed++;
                    break;
                case ProjectPromptAddendumRemoved removed:
                    (committer, signingKey) = await EnsureIdentityAsync(session, project, committer, signingKey, cancellationToken);
                    await DeleteAsync(project.RepositoryPath, removed, committer, signingKey, trustChain, cancellationToken);
                    pushed++;
                    break;
            }
        }

        long newPosition = candidates[^1].Sequence;
        if (position is null)
        {
            session.Store(new PromptAddendaSyncPosition { Id = project.Id, LastScannedGlobalSequence = newPosition });
        }
        else
        {
            position.LastScannedGlobalSequence = newPosition;
            session.Store(position);
        }

        await session.SaveChangesAsync(cancellationToken);
        return pushed;
    }

    private async Task<(LedgerCommitter Committer, LedgerSigningKey SigningKey)> EnsureIdentityAsync(
        IDocumentSession session, ProjectDetails project, LedgerCommitter? committer, LedgerSigningKey? signingKey,
        CancellationToken cancellationToken)
    {
        if (committer is not null && signingKey is not null)
        {
            return (committer, signingKey);
        }

        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(project.OwnerId, token: cancellationToken)
            ?? throw new InvalidOperationException($"No owner {project.OwnerId} for project {project.Id}.");
        NodeSigningKey key = await keyStore.EnsureAsync(node.NodeId, cancellationToken);
        return (
            new LedgerCommitter(
                owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
                owner.Email.IsNotBlank() ? owner.Email : $"{node.NodeId}@hall9k.local"),
            new LedgerSigningKey(key.PrivateKeyPath));
    }

    private async Task WriteAsync(
        string repositoryPath, ProjectPromptAddendumSet set, LedgerCommitter committer, LedgerSigningKey signingKey,
        TrustChain trustChain, CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.PromptAddenda.RefspecSource;
        string path = LedgerRefRegistry.PromptAddendumPath(set.BuilderKey);
        string content = set.OverCap ? $"{ProjectPromptAddendaLoader.OverCapMarker}\n{set.Content}" : set.Content;

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);

            // Content-equal to the tip is only a real no-op when the tip's own newest commit is
            // itself owner-authorized: PushAsync only ever reaches here once this node's own owner
            // is confirmed Owner-role, so a tip whose newest commit for this path is NOT authorized
            // is a member's or an unsigned collaborator's overwrite sitting ahead of the owner's own
            // state, and MaterializeAsync already walks past it on every node. Skipping the write
            // here because the bytes happen to match would leave that unauthorized commit as the
            // permanent tip, so the owner's own re-assertion of the identical content — the
            // documented lost-node-then-reinstate flow — could otherwise never take effect
            // (independent pre-PR review, cycle 1, conformance and adversarial lenses, medium).
            if (current.Content == content
                && await NewestCommitIsOwnerAuthorizedAsync(repositoryPath, refName, trustChain, cancellationToken))
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId, $"Set {set.BuilderKey} prompt addendum",
                    committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"{path} kept changing out from under the prompt-addenda sync after {MaxConflictRetries} attempts; "
            + "will retry next sweep.");
    }

    private async Task DeleteAsync(
        string repositoryPath, ProjectPromptAddendumRemoved removed, LedgerCommitter committer,
        LedgerSigningKey signingKey, TrustChain trustChain, CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.PromptAddenda.RefspecSource;
        string path = LedgerRefRegistry.PromptAddendumPath(removed.BuilderKey);

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);

            // The mirror of WriteAsync's own check: an absent file at the tip is only a real no-op
            // when the newest commit that touched this path was itself owner-authorized. A member's
            // or an unsigned collaborator's own delete sitting at the tip is unauthorized, so
            // MaterializeAsync already walks past it and restores the owner's own last content —
            // but returning here without ever writing left the owner's own removal silently
            // swallowed: the sync position still advanced past the event, so it was never retried,
            // and the member's delete kept reaching every node's agents forever (independent pre-PR
            // review, cycle 1, conformance and adversarial lenses, medium).
            if (!current.Exists
                && await NewestCommitIsOwnerAuthorizedAsync(repositoryPath, refName, trustChain, cancellationToken))
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.DeleteAsync(
                new LedgerDeleteRequest(
                    repositoryPath, refName, path, current.BlobId, $"Remove {removed.BuilderKey} prompt addendum",
                    committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"{path} kept changing out from under the prompt-addenda sync after {MaxConflictRetries} attempts; "
            + "will retry next sweep.");
    }

    /// <summary>
    /// Per builder path, materializes the state at the NEWEST commit touching that path that passes
    /// the owner test (idea 6be68ee2, trust-ledger finding 6) — signed by a root key or a currently
    /// vouched node key of some member whose <see cref="TrustChain.RoleOf"/> is
    /// <see cref="MembershipRole.Owner"/> (<see cref="OwnerChainAuthorization.IsAuthorizedByAnyOwnerRoleMemberAsync"/>,
    /// the identical rule <see cref="GitLedgerChainReader"/> applies to the members ref itself) — and
    /// keeps this node's local disk copy in step — the copy every prompt builder's loader actually
    /// reads. A member's overwrite, a member's delete, an unsigned commit from a repository
    /// collaborator, and a revoked node's commit are all walked PAST rather than trusted, so the
    /// owner's own last authorized state is restored rather than removed or overwritten. Read-only
    /// against the ledger itself; only ever writes local disk.
    /// <para>
    /// A cheap <see cref="ILedger.ListRefsAsync"/> (<c>git ls-remote</c>, no fetch) decides whether
    /// this project's prompt-addenda ref moved since this node's last look; only a moved tip, OR a
    /// changed owner-role key set (<see cref="ComputeOwnerRoleKeySetSignature"/> — a node revoked or
    /// an owner demoted since the last look), pays for walking every builder's own commit history.
    /// </para>
    /// <para>
    /// The tip is banked into <see cref="_lastKnownPromptAddendaTips"/> only once this call is about
    /// to return successfully — never as soon as it is read — so a throw anywhere in between (a
    /// commit read, or a <see cref="File.WriteAllTextAsync(string,string,CancellationToken)"/>/
    /// <see cref="File.Delete(string)"/> partway through the builder loop) leaves the cache exactly
    /// as it was. The next tick then sees the same "unmoved" state it saw before this attempt and
    /// retries the whole materialize from scratch, matching what <see cref="SweepOnceAsync"/>'s own
    /// catch already tells the log it will do — the identical fail-closed pattern
    /// <c>MessageSweepEngine.SweepOnceAsync</c> already applies to its own per-project trust chain
    /// read: a failure here (banked up front, before any local disk work starts) leaves the last
    /// good files standing rather than losing or corrupting them.
    /// </para>
    /// </summary>
    private async Task<int> MaterializeAsync(ProjectDetails project, TrustChain trustChain, CancellationToken cancellationToken)
    {
        if (!project.HomeDirectory.HasValue)
        {
            return 0;
        }

        string home = project.HomeDirectory.Value;
        string refName = LedgerRefRegistry.PromptAddenda.RefspecSource;
        (string RepositoryPath, string Home) tipKey = (project.RepositoryPath, home);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (IsBackingOffListRefs(project.Id, now))
        {
            return 0;
        }

        IReadOnlyList<LedgerRef> refs;
        try
        {
            refs = await ledger.ListRefsAsync(project.RepositoryPath, refName, cancellationToken);
        }
        catch
        {
            RecordListRefsFailure(project.Id, now);
            throw;
        }

        ClearListRefsBackoff(project.Id);
        string? tip = refs.FirstOrDefault(reference => reference.RefName == refName)?.Sha;
        string ownerRoleKeySet = ComputeOwnerRoleKeySetSignature(trustChain);

        if (_lastKnownPromptAddendaTips.TryGetValue(tipKey, out (string? Tip, string OwnerRoleKeySet) known)
            && known.Tip == tip && known.OwnerRoleKeySet == ownerRoleKeySet)
        {
            return 0;
        }

        int changed = 0;
        bool loggedNoOwnerRoleMemberWarning = false;
        foreach (PromptBuilderKey builder in PromptBuilderKey.All)
        {
            string localPath = ProjectHomePaths.PromptAddendumFile(home, builder);
            string? content = null;

            if (tip is not null && ownerRoleKeySet.Length == 0)
            {
                // No Owner-role member exists for this project at all (no genesis, or this node
                // never ran `h9k project join`) — every commit fails the owner test for the same
                // reason, so naming the newest refused commit per builder below would just repeat
                // an identical, uninformative line four times. Named once, with the actual cause
                // and the actual fix, rather than leaving the operator to infer "no owner chain"
                // from a commit sha that could just as easily mean an ordinary member overwrite
                // (independent pre-PR review, cycle 3, adversarial lens, medium).
                if (!loggedNoOwnerRoleMemberWarning)
                {
                    logger.LogWarning(
                        "Prompt-addenda materialize found no Owner-role member for project {ProjectId} at all: "
                        + "{Ref} cannot ever be authorized until this node runs 'h9k project join', so any "
                        + "already-materialized addenda are being removed rather than trusted unauthenticated",
                        project.Id, refName);
                    loggedNoOwnerRoleMemberWarning = true;
                }
            }
            else if (tip is not null)
            {
                string path = LedgerRefRegistry.PromptAddendumPath(builder.Value);
                IReadOnlyList<LedgerPathCommit> commits = await commitReader.ReadCommitsTouchingPathAsync(
                    project.RepositoryPath, refName, path,
                    (rawCommitBytes, token) =>
                        IsAuthorizedByAnyOwnerRoleMemberAsync(project.RepositoryPath, rawCommitBytes, trustChain, token),
                    cancellationToken);

                // Stops at the first authorized commit rather than checking every remaining, older
                // one in the path's whole history: SelectMaterializedContent only ever picks the
                // FIRST authorized entry out of a newest-first list, so an older commit's own
                // verdict — authorized or not — can never change the result once one is found, and
                // each check costs a real signature verification in production
                // (GitLedgerCommitReader.IsSignedByAsync). ReadCommitsTouchingPathAsync's own early
                // stop already applies the identical rule to how much of the ref's real history it
                // ever reads off disk in the first place.
                List<PromptAddendumCommitVerdict> verdicts = new(commits.Count);
                foreach (LedgerPathCommit commit in commits)
                {
                    bool authorized = await IsAuthorizedByAnyOwnerRoleMemberAsync(
                        project.RepositoryPath, commit.RawCommitBytes, trustChain, cancellationToken);
                    verdicts.Add(new PromptAddendumCommitVerdict(commit.CommitSha, commit.Content, authorized));
                    if (authorized)
                    {
                        break;
                    }
                }

                content = SelectMaterializedContent(verdicts);

                // Logged once per tip (this whole method only reaches here on a moved tip or
                // owner-role key set — the cache check above) rather than once per skipped commit:
                // the newest commit is the one an operator actually pushed and had refused, so
                // naming it — never the whole skipped chain behind it — is what "the refusal is
                // logged once per tip with ref, path, commit, and reason" asks for. No
                // UnverifiedLedgerWrite here: h9k project members already prints the chain reader's
                // own live list, and a persisted record for this sweep would flap on every tick a
                // member keeps pushing over it.
                if (verdicts.Count > 0 && !verdicts[0].AuthorizedByOwner)
                {
                    logger.LogWarning(
                        "Prompt-addenda write refused for project {ProjectId}: {Ref} {Path} at commit {Commit} is "
                        + "not signed by a root key or a currently vouched node key of an Owner-role member; the "
                        + "newest authorized state is materialized instead",
                        project.Id, refName, path, verdicts[0].CommitSha);
                }
            }

            if (content is null)
            {
                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                    changed++;
                }

                continue;
            }

            if (!File.Exists(localPath) || await File.ReadAllTextAsync(localPath, cancellationToken) != content)
            {
                Directory.CreateDirectory(ProjectHomePaths.PromptAddendaDirectory(home));
                await File.WriteAllTextAsync(localPath, content, cancellationToken);
                changed++;
            }
        }

        _lastKnownPromptAddendaTips[tipKey] = (tip, ownerRoleKeySet);
        return changed;
    }

    /// <summary>
    /// A deterministic signature over every root and vouched-node key of every currently Owner-role
    /// member — order-independent (sorted before joining), so the identical live set always signs
    /// identically regardless of dictionary enumeration order. Changes the moment a node is revoked,
    /// re-vouched, or an owner is promoted or demoted, which is exactly what
    /// <see cref="_lastKnownPromptAddendaTips"/> needs to re-materialize on.
    /// </summary>
    private static string ComputeOwnerRoleKeySetSignature(TrustChain trustChain)
    {
        IEnumerable<string> keys = trustChain.OwnerChains
            .Where(pair => trustChain.RoleOf(pair.Key) == MembershipRole.Owner)
            .SelectMany(pair => new[] { pair.Value.RootPublicKeyLine }.Concat(pair.Value.Nodes.Select(node => node.PublicKeyLine)))
            .OrderBy(key => key, StringComparer.Ordinal);
        return string.Join('\n', keys);
    }

    /// <summary>
    /// Whether the ref's own literal tip commit — <see cref="ILedgerCommitReader.IsRefTipAuthorizedAsync"/>,
    /// never <see cref="ILedgerCommitReader.ReadCommitsTouchingPathAsync"/>'s own path-scoped walk
    /// — was itself signed by a root key or a currently vouched node key of an Owner-role member.
    /// <see cref="WriteAsync"/> and <see cref="DeleteAsync"/> call this only to decide whether
    /// skipping an already-matching tip is safe, never to decide what content to materialize, and
    /// that decision has to be about the ref's own current state as a whole, not about whichever
    /// one builder path this particular write or delete happens to concern: a tip written for some
    /// OTHER builder is not evidence either way about whether THIS builder's own last write was
    /// authorized, so filtering the tip check by path here would answer a question this caller
    /// never asked (independent pre-PR review, cycle 5, conformance and adversarial lenses, both
    /// high, on the sibling defect this same conflation caused in <see cref="MaterializeAsync"/>'s
    /// own walk). No commit at all counts as authorized when the ref does not exist yet — there is
    /// nothing at the tip to override.
    /// </summary>
    private async Task<bool> NewestCommitIsOwnerAuthorizedAsync(
        string repositoryPath, string refName, TrustChain trustChain, CancellationToken cancellationToken) =>
        await commitReader.IsRefTipAuthorizedAsync(
            repositoryPath, refName,
            (rawCommitBytes, token) => IsAuthorizedByAnyOwnerRoleMemberAsync(repositoryPath, rawCommitBytes, trustChain, token),
            cancellationToken);

    /// <summary>The one owner-authorization check every call site above shares, closed over
    /// <see cref="commitReader"/>'s own <see cref="ILedgerCommitReader.IsSignedByAsync"/> — pulled
    /// out so <see cref="ILedgerCommitReader.ReadCommitsTouchingPathAsync"/>'s own early-stop
    /// predicate and each caller's post-hoc verdict use the identical rule rather than two
    /// hand-written copies of it drifting apart.</summary>
    private Task<bool> IsAuthorizedByAnyOwnerRoleMemberAsync(
        string repositoryPath, string rawCommitBytes, TrustChain trustChain, CancellationToken cancellationToken) =>
        OwnerChainAuthorization.IsAuthorizedByAnyOwnerRoleMemberAsync(
            trustChain,
            (key, token) => commitReader.IsSignedByAsync(repositoryPath, rawCommitBytes, key, token),
            cancellationToken);

    /// <summary>
    /// Pure reducer over one builder path's own commit history, newest first (the order
    /// <see cref="ILedgerCommitReader.ReadCommitsTouchingPathAsync"/> reports): the content to
    /// materialize is whatever the NEWEST authorized commit produced — null when that commit deleted
    /// the path, or when nothing in the whole list was ever authorized. A member's overwrite or
    /// delete sitting ahead of an owner's own last authorized commit is simply skipped over: the
    /// owner's own last state is restored, never removed (idea 6be68ee2, trust-ledger finding 6).
    /// </summary>
    internal static string? SelectMaterializedContent(IReadOnlyList<PromptAddendumCommitVerdict> commitsNewestFirst) =>
        commitsNewestFirst.FirstOrDefault(commit => commit.AuthorizedByOwner).Content;
}

/// <summary>One ledger commit touching a prompt-addendum path, paired with whether
/// <see cref="OwnerChainAuthorization.IsAuthorizedByAnyOwnerRoleMemberAsync"/> authorized it —
/// <see cref="PromptAddendaSweepEngine.SelectMaterializedContent"/>'s own input shape.</summary>
internal readonly record struct PromptAddendumCommitVerdict(string CommitSha, string? Content, bool AuthorizedByOwner);
