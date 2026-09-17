using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
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
    ILogger<PromptAddendaSweepEngine> logger)
{
    private const int MaxConflictRetries = 5;

    /// <summary>Each project's own prompt-addenda ref tip as of this node's last look — so a tick
    /// that finds an unmoved tip skips the fetch <see cref="ILedger.ReadAllAsync"/> would otherwise
    /// cost, the same tip-gating <c>InviteSweepEngine._lastKnownRefs</c> already uses for exactly
    /// this reason. In-memory and per-process by design: a restart just re-reads once, cheap and
    /// correct, never lossy (independent pre-PR review, cycle 1, conformance and adversarial
    /// lenses, both medium: every tick otherwise fetched all four builder paths individually, once
    /// per project, forever, even for a project that has never had an addendum).</summary>
    private readonly Dictionary<string, string?> _lastKnownPromptAddendaTips = [];

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
            try
            {
                int pushedForProject = await PushAsync(project, cancellationToken);
                pushed += pushedForProject;
                if (pushedForProject > 0)
                {
                    // A push that actually landed only ever follows a position that had not yet
                    // advanced past it — the same un-advanced position a still-outstanding failure
                    // leaves behind (PushAsync's own doc) — so reaching here at all means whatever
                    // failure this project was carrying, if any, no longer applies.
                    await ClearPushFailureAsync(project.Id, cancellationToken);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Prompt-addenda push failed for project {ProjectId}; will retry next sweep", project.Id);
                await RecordPushFailureAsync(project.Id, exception.Message, cancellationToken);
            }

            try
            {
                materialized += await MaterializeAsync(project, cancellationToken);
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

    /// <summary>The success-path mirror of <see cref="RecordPushFailureAsync"/>: nothing to do
    /// when this project never had a recorded failure, so the ordinary, always-succeeding case
    /// never pays for a document load and save it does not need.</summary>
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
    /// </summary>
    private async Task<int> PushAsync(ProjectDetails project, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

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

        int pushed = 0;
        LedgerCommitter? committer = null;
        LedgerSigningKey? signingKey = null;

        foreach (IEvent candidate in candidates)
        {
            switch (candidate.Data)
            {
                case ProjectPromptAddendumSet set:
                    (committer, signingKey) = await EnsureIdentityAsync(session, project, committer, signingKey, cancellationToken);
                    await WriteAsync(project.RepositoryPath, set, committer, signingKey, cancellationToken);
                    pushed++;
                    break;
                case ProjectPromptAddendumRemoved removed:
                    (committer, signingKey) = await EnsureIdentityAsync(session, project, committer, signingKey, cancellationToken);
                    await DeleteAsync(project.RepositoryPath, removed, committer, signingKey, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.PromptAddenda.RefspecSource;
        string path = LedgerRefRegistry.PromptAddendumPath(set.BuilderKey);
        string content = set.OverCap ? $"{ProjectPromptAddendaLoader.OverCapMarker}\n{set.Content}" : set.Content;

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
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
        LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = LedgerRefRegistry.PromptAddenda.RefspecSource;
        string path = LedgerRefRegistry.PromptAddendumPath(removed.BuilderKey);

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (!current.Exists)
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
    /// Reads the ledger's own current content for every builder key and keeps this node's local
    /// disk copy in step — the copy every prompt builder's loader actually reads. Read-only against
    /// the ledger itself; only ever writes local disk.
    /// <para>
    /// A cheap <see cref="ILedger.ListRefsAsync"/> (<c>git ls-remote</c>, no fetch) decides whether
    /// this project's prompt-addenda ref moved since this node's last look; only a moved tip pays
    /// for the real fetch <see cref="ILedger.ReadAllAsync"/> costs, and that one call reads every
    /// builder's file under <see cref="LedgerRefRegistry.PromptAddendaPathPrefix"/> at once rather
    /// than one <see cref="ILedger.ReadAsync"/> fetch per builder key.
    /// </para>
    /// </summary>
    private async Task<int> MaterializeAsync(ProjectDetails project, CancellationToken cancellationToken)
    {
        if (!project.HomeDirectory.HasValue)
        {
            return 0;
        }

        string home = project.HomeDirectory.Value;
        string refName = LedgerRefRegistry.PromptAddenda.RefspecSource;

        IReadOnlyList<LedgerRef> refs = await ledger.ListRefsAsync(project.RepositoryPath, refName, cancellationToken);
        string? tip = refs.FirstOrDefault(reference => reference.RefName == refName)?.Sha;

        if (_lastKnownPromptAddendaTips.TryGetValue(project.RepositoryPath, out string? knownTip) && knownTip == tip)
        {
            return 0;
        }

        IReadOnlyList<LedgerEntry> entries = tip is null
            ? []
            : await ledger.ReadAllAsync(project.RepositoryPath, refName, LedgerRefRegistry.PromptAddendaPathPrefix, cancellationToken);
        _lastKnownPromptAddendaTips[project.RepositoryPath] = tip;

        Dictionary<string, string> contentByBuilder = entries.ToDictionary(
            entry => Path.GetFileNameWithoutExtension(entry.Path), entry => entry.Content);

        int changed = 0;
        foreach (PromptBuilderKey builder in PromptBuilderKey.All)
        {
            string localPath = ProjectHomePaths.PromptAddendumFile(home, builder);

            if (!contentByBuilder.TryGetValue(builder.Value, out string? content))
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

        return changed;
    }
}
