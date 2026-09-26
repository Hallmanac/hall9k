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
    /// </summary>
    private readonly Dictionary<(string RepositoryPath, string Home), string?> _lastKnownPromptAddendaTips = [];

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

        // A replicated change can land on this stream behind a newer one for the same builder key (a
        // catch-up answer serves a pre-switch-on head after the tail), so a change stamped older than
        // the one the projection applied for its key is never pushed: it would overwrite the newer
        // file in the ledger. The projection is read after the events, so it has applied every event
        // scanned above.
        ProjectDetails? applied = await session.LoadAsync<ProjectDetails>(project.Id, cancellationToken);
        bool Superseded(string builderKey, DateTimeOffset stamp) =>
            applied is not null && applied.PromptAddendumStamps.TryGetValue(builderKey, out DateTimeOffset newest) && stamp < newest;

        int pushed = 0;
        LedgerCommitter? committer = null;
        LedgerSigningKey? signingKey = null;

        foreach (IEvent candidate in candidates)
        {
            switch (candidate.Data)
            {
                case ProjectPromptAddendumSet set when Superseded(set.BuilderKey, set.SetAt):
                case ProjectPromptAddendumRemoved removed when Superseded(removed.BuilderKey, removed.RemovedAt):
                    break;
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
    /// <para>
    /// The tip is banked into <see cref="_lastKnownPromptAddendaTips"/> only once this call is about
    /// to return successfully — never as soon as it is read — so a throw anywhere in between (the
    /// fetch itself, or a <see cref="File.WriteAllTextAsync(string,string,CancellationToken)"/>/
    /// <see cref="File.Delete(string)"/> partway through the builder loop) leaves the cache exactly
    /// as it was. The next tick then sees the same "unmoved" tip it saw before this attempt and
    /// retries the whole materialize from scratch, matching what <see cref="SweepOnceAsync"/>'s own
    /// catch already tells the log it will do. Banking the tip up front, before any of that local
    /// disk work even started, is what independent pre-PR review, cycle 1 (conformance and
    /// adversarial lenses, both medium) flagged: a failure partway through was banked as if it had
    /// fully succeeded, so the remaining builders' files stayed stale or missing for the life of the
    /// daemon process while the only trace was a warning claiming the opposite.
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
        (string RepositoryPath, string Home) tipKey = (project.RepositoryPath, home);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_listRefsBackoff.TryGetValue(project.Id, out (DateTimeOffset RetryAfter, TimeSpan Interval) backoff)
            && now < backoff.RetryAfter)
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
            TimeSpan nextInterval = backoff.Interval == default
                ? ListRefsBackoffFloor
                : TimeSpan.FromTicks(Math.Min(backoff.Interval.Ticks * 2, ListRefsBackoffCeiling.Ticks));
            _listRefsBackoff[project.Id] = (now + nextInterval, nextInterval);
            throw;
        }

        _listRefsBackoff.Remove(project.Id);
        string? tip = refs.FirstOrDefault(reference => reference.RefName == refName)?.Sha;

        if (_lastKnownPromptAddendaTips.TryGetValue(tipKey, out string? knownTip) && knownTip == tip)
        {
            return 0;
        }

        IReadOnlyList<LedgerEntry> entries = tip is null
            ? []
            : await ledger.ReadAllAsync(project.RepositoryPath, refName, LedgerRefRegistry.PromptAddendaPathPrefix, cancellationToken);

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

        _lastKnownPromptAddendaTips[tipKey] = tip;
        return changed;
    }
}
