using System.ComponentModel;
using System.Globalization;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class ProjectJoinCommand : Hall9kAsyncCommand<ProjectJoinCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PROJECT>")]
        [Description("Project name, an unambiguous fragment of it, or its id.")]
        public string Project { get; init; } = string.Empty;

        [CommandOption("--owner <FINGERPRINT>")]
        [Description(
            "Claim an existing root's fingerprint as this node's owner instead of establishing a new "
            + "one. Recorded on the node file and the local owner record unverified, until an already-"
            + "enrolled node of that owner confirms it: a hand-run h9k node vouch, or the minting "
            + "node's own daemon sweep matching a claimed h9k node invite. Omit it on a genesis node: the first join "
            + "with no --owner establishes this owner's root using this node's own key. Re-runnable: "
            + "joining again with a different fingerprint changes the claim, retiring a self-created "
            + "root when this node turns out to belong to another one.")]
        public string? Owner { get; init; }

        [CommandOption("--invite <SECRET>")]
        [Description(
            "A single-use secret from h9k node invite or h9k project invite (idea 202383dc, T2). Proves "
            + "possession by writing HMAC(secret, this node's own key fingerprint) into this node's own node "
            + "file's proof field; the minting node's own daemon sweep matches it and vouches this node in, no "
            + "further prompt needed. A node-of-owner invite claims that invite's own owner (like --owner, but "
            + "read from the secret — combining the two is refused) and creates no root; a member-of-project "
            + "invite creates this node's own root when it has none yet, same as an ordinary --owner-less join. "
            + "Refused if the invite is not found in this project's own ledger, already spent, or expired.")]
        public string? Invite { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);

        // Refreshed here, before the join itself, the same live call h9k project add already makes
        // right before it needs to know its own account is confirmed: an install whose GitHub
        // connection predates this task's own identity read (or one bootstrapped while gh was
        // unauthenticated) has no other path to ever gain a confirmed account, so without this every
        // join on every project it already registered is refused forever below, even with gh fully
        // authenticated right now — the refusal's own advice ("h9k project add", "h9k connection
        // list") does not fix that either, since neither call reads gh (independent pre-PR review,
        // cycle 3, conformance and adversarial lenses, both high). Kept in this untested live
        // entry point rather than the internal RunAsync below: RunAsync's own doc comment is
        // Brian's 2026-09-13 rule that it never touches a real gh/network call, and
        // NodeBootstrap.RefreshGitHubIdentityAsync has no ProcessRunner seam yet
        // (NodeContext.InitializeAsync's own comment on the identical constraint) — h9k project
        // add's own auto-join (ProjectAddCommand.TryJoinAsync) calls RunAsync directly for the same
        // reason and already gets this from ProjectAddCommand.ExecuteAsync's own earlier call, so
        // this is only reached standalone. Best-effort: a gh that cannot answer leaves the
        // connection's already-recorded identity exactly as it was.
        // Ambient (no account pinned): a node's own very first join can be this install's genesis
        // bootstrap too, before any GitHub connection is confirmed, so there is no account yet for
        // ProjectGitHubClient to resolve and run this as.
        GhIdentityReader ghIdentityReader = ProjectGitHubClient.AmbientIdentityReader(Environment.CurrentDirectory);
        BootstrapContext refreshContext = await NodeBootstrap.EnsureAsync(session, cancellationToken, ghIdentityReader);
        await NodeBootstrap.RefreshGitHubIdentityAsync(session, refreshContext.ConnectionId, cancellationToken, ghIdentityReader);
        await session.SaveChangesAsync(cancellationToken);

        JoinOutcome outcome;
        try
        {
            outcome = await RunAsync(session, project, settings.Owner, settings.Invite, cancellationToken);
        }
        // A1's own git plumbing throws these as plain, undecorated exceptions rather than a
        // Domain*Exception — fine for h9k project add's own TryJoinAsync, which already wraps
        // every exception broadly and reports it as best-effort, but this standalone command has
        // no such wrapper, so either one previously escaped as an unhandled crash with a stack
        // trace instead of the "why" on stderr AGENTS.md's CLI standard requires (adversarial
        // review, cycle 1, low).
        catch (Exception exception) when (exception is LedgerPushRejectedException or InvalidOperationException)
        {
            throw new DomainValidationException(
                $"h9k project join could not finish against '{project.Name}'s own ledger: "
                + $"{exception.Message} Re-run h9k project join {project.Name} once that settles.");
        }

        Report(project, outcome);
        return ExitCodes.Ok;
    }

    /// <summary>What one join produced, for both the command's own report and h9k project add's condensed one.</summary>
    internal sealed record JoinOutcome(
        Guid NodeId,
        string KeyFingerprint,
        string PrivateKeyPath,
        string ClaimedOwnerFingerprint,
        bool EstablishedRoot,
        bool RetiredPreviousRoot,
        bool WroteNodeFile,
        bool OwnerClaimChanged);

    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, CancellationToken cancellationToken) =>
        RunAsync(session, project, claimedOwnerOverride, invite: null, cancellationToken);

    /// <summary>The invite-aware overload (idea 202383dc, T2) — everything <see cref="RunAsync(IDocumentSession,ProjectDetails,string?,CancellationToken)"/>
    /// already does, plus proving possession of a minted secret when one is given.</summary>
    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, string? invite,
        CancellationToken cancellationToken) =>
        RunAsync(
            session, project, claimedOwnerOverride, invite,
            new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new NodeKeyStore(),
            new ProjectGitHubAccessMirror(), cancellationToken);

    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session,
        ProjectDetails project,
        string? claimedOwnerOverride,
        ILedger ledger,
        NodeKeyStore keyStore,
        ProjectGitHubAccessMirror githubAccess,
        CancellationToken cancellationToken) =>
        RunAsync(session, project, claimedOwnerOverride, invite: null, ledger, keyStore, githubAccess, cancellationToken);

    /// <summary>
    /// The whole join flow, seamed on <see cref="ILedger"/>, <see cref="NodeKeyStore"/>, and
    /// <see cref="ProjectGitHubAccessMirror"/> so a test drives it against the in-memory ledger
    /// fake, a stubbed key generator, and a fake gh transport rather than a real repository, a real
    /// ssh-keygen invocation, or a real gh/network call (Brian's 2026-09-13 testing rule).
    /// </summary>
    internal static async Task<JoinOutcome> RunAsync(
        IDocumentSession session,
        ProjectDetails project,
        string? claimedOwnerOverride,
        string? invite,
        ILedger ledger,
        NodeKeyStore keyStore,
        ProjectGitHubAccessMirror githubAccess,
        CancellationToken cancellationToken)
    {
        if (claimedOwnerOverride.IsNotBlank() && invite.IsNotBlank())
        {
            throw new DomainValidationException(
                "--owner and --invite are refused together — an invite already names the owner it claims "
                + "(node-of-owner) or claims none at all (member-of-project); pass one or the other.");
        }

        if (claimedOwnerOverride.IsNotBlank() && !NodeKeyStore.IsFingerprint(claimedOwnerOverride))
        {
            throw new DomainValidationException(
                $"'{claimedOwnerOverride}' is not a fingerprint h9k project join can claim — a fingerprint "
                + "is the 64-character lowercase hex SHA-256 h9k owner show prints for a root, not "
                + "something typed by hand. Check the value with the owner who ran h9k owner show on the "
                + "genesis node.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        // Flushed before aggregating: on a brand-new install this is the same call that just
        // started the Owner and Node streams, and live aggregation reads the database, not this
        // session's own not-yet-saved pending events.
        await session.SaveChangesAsync(cancellationToken);

        // Refused before any key is generated or any ledger byte is written: a node cannot write
        // this project's ledger at all without push on the repository it lives in, so there is
        // nothing to be gained by getting further before finding that out (idea 202383dc, A2b,
        // item 2). The same gh round trip also observes this install's own role, and the
        // collaborator list when it is readable, onto the project's own stream (item 3).
        ProjectGitHubAccessResult access = await githubAccess.ObserveAsync(session, project, now, cancellationToken);

        // Saved before the push refusal below can throw: this install's own role is the one fact
        // every install gets regardless of its own role (ProjectGitHubMembers's own doc comment), so
        // an install stuck below push must still have it recorded rather than lose the observation
        // to an exception thrown before anything reached the database (independent pre-PR review,
        // cycle 1, conformance and adversarial lenses, both medium).
        await session.SaveChangesAsync(cancellationToken);

        if (!access.OwnRole.HasPush)
        {
            throw new DomainValidationException(
                $"This install's GitHub account has no push on {access.Repository} (role: "
                + $"{(access.OwnRole == GitHubRepositoryRole.Unknown ? "none observed" : access.OwnRole.Value)}). "
                + "A node cannot write this project's ledger without push on its own repository — ask an "
                + $"admin on {access.Repository} to grant it, then retry h9k project join.");
        }

        NodeAggregate node = await session.Events.AggregateStreamAsync<NodeAggregate>(context.NodeId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No node {context.NodeId}.");
        OwnerAggregate owner = await session.Events.AggregateStreamAsync<OwnerAggregate>(context.OwnerId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No owner {context.OwnerId}.");

        // Signing is mandatory from here on: nothing below ever calls ILedger.WriteAsync without
        // this key, and a node that cannot produce one is refused right here, naming the command
        // that fixes it (NodeKeyStore.EnsureAsync's own message).
        NodeSigningKey key = await keyStore.EnsureAsync(context.NodeId, cancellationToken);

        if (node.PublicKey is null)
        {
            session.Events.Append(context.NodeId, NodeDecider.RegisterKey(node, key.PublicKeyLine, key.Fingerprint, now));
        }
        else if (node.PublicKey != key.PublicKeyLine)
        {
            throw new DomainConflictException(
                $"Node {context.NodeId}'s recorded public key no longer matches the key on disk at "
                + $"{NodeKeyStore.DirectoryFor(context.NodeId)} — something replaced the key files outside "
                + "h9k. Restore the original key files before joining again.");
        }

        // Validated and read before claimedOwnerOverride is used to compute claimedFingerprint
        // below, so a node-of-owner invite's own root can drive that computation exactly the way
        // an explicit --owner already does — and before any ledger byte is written, so a spent,
        // expired, or garbled invite is refused with nothing left to unwind (idea 202383dc, T2:
        // "a spent or expired invite is refused at join").
        string? inviteProof = null;
        string? inviteMinterRoot = null;
        if (invite.IsNotBlank())
        {
            if (!InviteSecret.TryParse(invite, out string inviteRoot, out Guid inviteId))
            {
                throw new DomainValidationException(
                    $"'{invite}' is not a recognized invite secret — h9k node invite/h9k project invite print "
                    + "the exact value to pass here.");
            }

            string inviteRefName = InviteLedgerRecord.RefName(inviteRoot);
            string invitePath = InviteLedgerRecord.PathFor(inviteRoot, inviteId);
            LedgerFile inviteFile = await ledger.ReadAsync(project.RepositoryPath, inviteRefName, invitePath, cancellationToken);
            InviteLedgerRecord? inviteRecord = InviteLedgerRecord.Parse(inviteFile.Content);
            if (inviteRecord is null)
            {
                throw new DomainValidationException(
                    $"No invite {inviteId} found in '{project.Name}'s own ledger — it may have been minted into "
                    + "a different project, or this project's own copy has not been fetched yet.");
            }

            if (inviteRecord.SecretHash != InviteSecret.Hash(invite))
            {
                throw new DomainValidationException(
                    $"'{invite}' does not match invite {inviteId}'s own recorded secret — check it was typed "
                    + "or pasted correctly.");
            }

            if (inviteRecord.Spent)
            {
                throw new DomainValidationException($"Invite {inviteId} is already spent — single use, idea 202383dc T2.");
            }

            if (inviteRecord.ExpiresAt <= now)
            {
                throw new DomainValidationException(
                    $"Invite {inviteId} expired at {inviteRecord.ExpiresAt:u} — ask the minting node for a fresh one.");
            }

            inviteProof = InviteSecret.ComputeProof(invite, key.Fingerprint);
            inviteMinterRoot = inviteRoot;
            if (inviteRecord.Claim == InviteClaimKind.NodeOfOwner)
            {
                // Mirrors an explicit --owner exactly: an unverified claim, no root established —
                // "a join with an invite creates no root when the claim is node-of-owner".
                claimedOwnerOverride = inviteRoot;
            }
            // A member-of-project claim leaves claimedOwnerOverride untouched: null falls through
            // to the ordinary "no --owner" path below, which keeps this node's own existing root
            // if it has one, or establishes a fresh one if it does not — "creates the joiner's
            // root when the claim is member-of-project and the joiner has none".
        }

        // No --owner keeps whatever root this owner already claims (including one claimed on a
        // different project's join) rather than falling back to this node's own key — falling back
        // unconditionally would flip a real owner's claim back to a self-created root on every
        // later plain join (independent pre-PR review, cycle 1, conformance and adversarial lenses,
        // both high: h9k project add's own join call always passes no --owner).
        string claimedFingerprint = claimedOwnerOverride.IsNotBlank()
            ? claimedOwnerOverride
            : owner.RootFingerprint ?? key.Fingerprint;
        // An explicit --owner always stays an unverified claim, even when it happens to name this
        // node's own fingerprint — establishing a root and marking it verified is reserved for the
        // no-argument path that lets a node's own key become the root in the first place.
        bool establishingRoot = claimedOwnerOverride.IsBlank() && claimedFingerprint == key.Fingerprint;

        LedgerCommitter committer = new(
            owner.Name.IsNotBlank() ? owner.Name : Environment.UserName,
            owner.Email.IsNotBlank() ? owner.Email : $"{context.NodeId}@hall9k.local");
        LedgerSigningKey signingKey = new(key.PrivateKeyPath);

        bool retiredPreviousRoot = false;
        if (owner.RootFingerprint != claimedFingerprint)
        {
            if (owner.RootFingerprintVerified && owner.RootFingerprint == key.Fingerprint)
            {
                // The owner's root claim is install-wide, but a self-created root.yaml may live in
                // every project's own ledger this node joined before the real owner was known — not
                // only the one project being joined right now, and not necessarily that one at all.
                // Retiring only "here" either leaves a live self-created root in every other project
                // (never retired), or writes a retired.yaml into a project that never had a root.yaml
                // to begin with (independent pre-PR review, cycle 1, conformance and adversarial
                // lenses, both medium).
                retiredPreviousRoot = await RetireSelfRootEverywhereAsync(
                    session, ledger, owner.Id, project, owner.RootFingerprint!, claimedFingerprint,
                    committer, signingKey, now, cancellationToken);
            }

            session.Events.Append(context.OwnerId, OwnerDecider.ClaimRoot(owner, claimedFingerprint, establishingRoot, now));
        }

        if (establishingRoot)
        {
            await EnsureRootFileAsync(
                ledger, project.RepositoryPath, claimedFingerprint, key.PublicKeyLine, now,
                committer, signingKey, cancellationToken);

            // Genesis (idea 202383dc, T1): the node establishing a brand-new root is this
            // project's own first owner-role member — self-written, the members ref's folder
            // still empty. Project-scoped, unlike the root file above: a root is owner-wide (every
            // project this owner joins shares one root.yaml lineage), but membership is this
            // project's own fact alone, so it is only ever written here, never elsewhere.
            bool wroteGenesisMember = await EnsureGenesisMemberFileAsync(
                ledger, project.RepositoryPath, claimedFingerprint, now, committer, signingKey, cancellationToken);
            if (wroteGenesisMember)
            {
                session.Events.Append(
                    project.Id, ProjectDecider.VouchMember(project.Id, claimedFingerprint, ProjectMemberRole.Owner, now));
            }
        }

        bool wroteNodeFile = await WriteNodeFileAsync(
            ledger, project.RepositoryPath, context.NodeId, key, claimedFingerprint,
            node.MachineName, node.OperatingSystem, node.KeyRegisteredAt ?? now, inviteProof,
            committer, signingKey, cancellationToken);

        // A node's own claim is install-wide (the Node stream), but node.yaml is only ever
        // rewritten in the project being joined right now, above — unlike root retirement, which
        // deliberately runs in every project this owner is registered to. A node.yaml already
        // written into some other, earlier-joined project under the old claim is left stale, so a
        // prior non-null claim that actually changes here is reported rather than left silent
        // (independent pre-PR review, cycle 1, conformance lens, low).
        bool ownerClaimChanged = node.ClaimedOwnerFingerprint is not null && node.ClaimedOwnerFingerprint != claimedFingerprint;
        if (node.ClaimedOwnerFingerprint != claimedFingerprint)
        {
            session.Events.Append(context.NodeId, NodeDecider.ClaimOwner(node, claimedFingerprint, now));
        }

        await session.SaveChangesAsync(cancellationToken);

        // Optional nudge (idea 202383dc, T2: "an optional note-kind message nudges the minting
        // node's owner when a proof appears; the sweep does not depend on it") — queued only,
        // never flushed here (this command never touches git or a network), and never allowed to
        // fail the join itself. In practice this node is never yet vouched into anything at this
        // point, so the minting node's own message transport always reads it as SenderNotVouched
        // and drops it (GitLedgerMessageTransport.ReadSinceAsync computes the trust chain before
        // it will read a sender at all) — it cannot arrive before the very vouch it exists to
        // announce, only after, when it no longer adds anything. Left in place as a harmless,
        // best-effort artifact rather than a working early nudge (independent pre-PR review, cycle
        // 1, conformance lens, low): the minting node's own sweep finds the proof on its own
        // regardless, which is the only thing this invite flow actually depends on.
        if (inviteProof is not null && inviteMinterRoot is not null)
        {
            try
            {
                await MessageOutbox.QueueAsync(
                    session, context.NodeId, claimedFingerprint, MessageAudience.Owner(inviteMinterRoot), about: null,
                    MessageKind.Note, $"Invite proof written for node {context.NodeId} in '{project.Name}'.", now,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Best-effort only, and deliberately broad: MessageOutbox.QueueAsync's own
                // SaveChangesAsync can fail in ways this call site cannot enumerate (a transient
                // Marten/Postgres error, not only the domain exceptions a validation failure would
                // throw), and none of them may ever fail the join whose own work — the root claim,
                // the node file with its proof — already landed above. The sweep's own ledger read
                // is what actually vouches this invite in, never this message.
                AnsiConsole.MarkupLine($"[yellow]Could not queue the invite nudge ({exception.Message.EscapeMarkup()}) — skipped.[/]");
            }
        }

        return new JoinOutcome(
            context.NodeId, key.Fingerprint, key.PrivateKeyPath, claimedFingerprint,
            establishingRoot, retiredPreviousRoot, wroteNodeFile, ownerClaimChanged);
    }

    internal static void Report(ProjectDetails project, JoinOutcome outcome)
    {
        AnsiConsole.MarkupLine(
            $"[green]Joined '{project.Name.EscapeMarkup()}'.[/] Node [dim]{outcome.NodeId}[/], "
            + $"key [dim]{outcome.KeyFingerprint}[/] at [dim]{outcome.PrivateKeyPath.EscapeMarkup()}[/].");
        AnsiConsole.MarkupLine(outcome.EstablishedRoot
            ? $"[dim]This is this owner's root — {outcome.ClaimedOwnerFingerprint} is the owner id everywhere in Hall9k now.[/]"
            : $"[dim]Claimed owner {outcome.ClaimedOwnerFingerprint}, unverified until an already-enrolled node of that owner confirms it (h9k node vouch, or a matched h9k node invite).[/]");
        if (outcome.RetiredPreviousRoot)
        {
            AnsiConsole.MarkupLine("[yellow]This node's own previously self-created root was retired in favor of the claimed owner.[/]");
        }

        if (outcome.OwnerClaimChanged)
        {
            AnsiConsole.MarkupLine(
                "[yellow]This node's claimed owner changed. Any other project this node already joined still "
                + "has the old claim in its own node.yaml until h9k project join <project> runs there too.[/]");
        }
    }

    /// <summary>
    /// Retires <paramref name="retiredFingerprint"/>'s self-created root in every project this
    /// owner is registered to, but only where a <c>root.yaml</c> for it actually exists — a project
    /// this node never joined as itself never had one to retire. <paramref name="currentProject"/>
    /// is checked even when this session's own project query has not yet observed it (a same-session
    /// registration not yet visible to a fresh query).
    /// </summary>
    /// <remarks>
    /// Retiring in <paramref name="currentProject"/> is required — a failure there fails this join,
    /// the same as every other write it makes. Retiring in every <em>other</em> registered project
    /// is best-effort: an archived project is skipped outright, and an unreachable one (its remote
    /// deleted, or behind a VPN that is down) is reported and skipped rather than blocking the join
    /// the user actually asked for. A forgotten <c>--owner</c> is supposed to cost one rerun, not a
    /// detour through an unrelated project's own remote (independent pre-PR review, cycle 1,
    /// conformance and adversarial lenses, medium).
    /// </remarks>
    private static async Task<bool> RetireSelfRootEverywhereAsync(
        IDocumentSession session, ILedger ledger, Guid ownerId, ProjectDetails currentProject,
        string retiredFingerprint, string realRootFingerprint, LedgerCommitter committer,
        LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectDetails> ownedProjects = await session.Query<ProjectDetails>()
            .Where(p => p.OwnerId == ownerId)
            .ToListAsync(cancellationToken);

        string refName = $"refs/hall9k/ledger/owners/{retiredFingerprint}";
        string path = $"owners/{retiredFingerprint}/root.yaml";

        bool retiredAny = await RetireIfPresentAsync(
            ledger, currentProject, refName, path, retiredFingerprint, realRootFingerprint,
            committer, signingKey, now, cancellationToken);

        IEnumerable<ProjectDetails> otherProjects = ownedProjects
            .Where(p => p.Id != currentProject.Id && !p.IsArchived);
        foreach (ProjectDetails other in otherProjects)
        {
            try
            {
                retiredAny |= await RetireIfPresentAsync(
                    ledger, other, refName, path, retiredFingerprint, realRootFingerprint,
                    committer, signingKey, now, cancellationToken);
            }
            catch (Exception exception) when (exception is LedgerPushRejectedException or DomainConflictException or InvalidOperationException)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not retire the previous root in '{other.Name.EscapeMarkup()}' "
                    + $"({exception.Message.EscapeMarkup()}) — skipped. Re-run h9k project join {other.Name.EscapeMarkup()} "
                    + "once that project is reachable again.[/]");
            }
        }

        return retiredAny;
    }

    private static async Task<bool> RetireIfPresentAsync(
        ILedger ledger, ProjectDetails project, string refName, string path,
        string retiredFingerprint, string realRootFingerprint, LedgerCommitter committer,
        LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        LedgerFile existing = await ledger.ReadAsync(project.RepositoryPath, refName, path, cancellationToken);
        if (!existing.Exists)
        {
            return false;
        }

        await RetireSelfRootAsync(
            ledger, project.RepositoryPath, retiredFingerprint, realRootFingerprint,
            committer, signingKey, now, cancellationToken);
        return true;
    }

    private static async Task RetireSelfRootAsync(
        ILedger ledger, string repositoryPath, string retiredFingerprint, string realRootFingerprint,
        LedgerCommitter committer, LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{retiredFingerprint}";
        string path = $"owners/{retiredFingerprint}/retired.yaml";
        string content = BuildYaml(
            ("retired_at", now.ToString("o", CultureInfo.InvariantCulture)),
            ("real_root", realRootFingerprint));

        // content depends only on this call's own arguments, so a Conflict here is always safe to
        // retry against a fresh tip, the same reasoning WriteNodeFileAsync's own retry applies —
        // the caller above saves the owner's new root claim on the strength of this retirement
        // actually landing, so a Conflict silently discarded here would let that claim commit
        // while retired.yaml either still held stale content or never recorded the retirement at
        // all (Copilot review, PR #366).
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    $"Retire root {retiredFingerprint} in favor of {realRootFingerprint}", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new DomainConflictException(
            $"{path} kept changing out from under this join after {MaxConflictRetries} attempts — "
            + "something else is writing it at the same time. Re-run h9k project join once that settles.");
    }

    private static async Task EnsureRootFileAsync(
        ILedger ledger, string repositoryPath, string fingerprint, string publicKeyLine, DateTimeOffset createdAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/owners/{fingerprint}";
        string path = $"owners/{fingerprint}/root.yaml";

        LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
        if (current.Exists)
        {
            // Already established here — by this node, or found already present on a fresh fetch.
            return;
        }

        string content = BuildYaml(
            ("public_key", publicKeyLine),
            ("created_at", createdAt.ToString("o", CultureInfo.InvariantCulture)));

        await ledger.WriteAsync(
            new LedgerWriteRequest(repositoryPath, refName, path, content, ExpectedBlobId: null, $"Establish root {fingerprint}", committer, signingKey),
            cancellationToken);

        // A conflict here means another join won the race to establish the identical root between
        // the read above and this write — the root exists either way, which is what this call wanted.
    }

    /// <summary>
    /// Genesis (idea 202383dc, T1's own criterion 2): writes this project's first
    /// <c>members/&lt;fingerprint&gt;.yaml</c>, role owner — only when the members ref's own
    /// <c>members/</c> folder is entirely empty, never merely when this fingerprint's own file
    /// happens to be absent. A per-fingerprint check let a second, later node establishing its own
    /// fresh root self-claim ownership in a project that already had a real owner, since its own
    /// fingerprint's file was of course absent too (independent pre-PR review, cycle 1, conformance
    /// and adversarial lenses, medium) — <see cref="GitLedgerChainReader"/>'s own genesis rule is
    /// "the very first commit ever touching a members file", and this must refuse to write at all
    /// once that slot is spent, exactly the same criterion the chain reader will judge this write
    /// against.
    /// </summary>
    private static async Task<bool> EnsureGenesisMemberFileAsync(
        ILedger ledger, string repositoryPath, string fingerprint, DateTimeOffset issuedAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        const string refName = "refs/hall9k/ledger/members";
        string path = $"members/{fingerprint}.yaml";

        if (await ledger.HasAnyAsync(repositoryPath, refName, "members/", cancellationToken))
        {
            // Genesis was already spent — by this fingerprint's own earlier join, or by someone
            // else's — so this join is not this project's first member and must not self-claim
            // ownership. A re-run whose own file is already there also lands here, correctly, as a
            // no-op: HasAnyAsync is true either way. A cheap early exit only — the write below is
            // what actually enforces this, against a tip fetched fresh in the same attempt.
            return false;
        }

        string content = BuildYaml(
            ("root_fingerprint", fingerprint),
            ("role", ProjectMemberRole.Owner.Value),
            ("issued_at", issuedAt.ToString("o", CultureInfo.InvariantCulture)));

        // RequireEmptyPrefix, not just ExpectedBlobId, closes the race the check above cannot: two
        // joins racing to establish genesis under two different fingerprints would each see
        // members/ empty here and both proceed, since ExpectedBlobId alone only guards this write's
        // own path. The prefix is re-checked against the ref's own freshly-fetched tip inside
        // WriteAsync's own retry loop, so whichever push actually lands first is the only one that
        // can win (independent review finding).
        LedgerWriteOutcome outcome = await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, refName, path, content, ExpectedBlobId: null,
                $"Establish {fingerprint} as this project's first owner-role member", committer, signingKey,
                RequireEmptyPrefix: "members/"),
            cancellationToken);

        // A conflict here means another join won the race to establish the identical genesis
        // member between the read above and this write — the entry exists either way.
        return outcome.Verdict == LedgerWriteVerdict.Written;
    }

    /// <summary>How many times a conflicting ledger write retries against a fresh read before giving up.</summary>
    private const int MaxConflictRetries = 5;

    private static async Task<bool> WriteNodeFileAsync(
        ILedger ledger, string repositoryPath, Guid nodeId, NodeSigningKey key, string claimedOwnerFingerprint,
        string machineName, string operatingSystem, DateTimeOffset joinedAt, string? inviteProof,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string refName = $"refs/hall9k/ledger/nodes/{nodeId}";
        string path = $"nodes/{nodeId}/node.yaml";

        string content = BuildYaml(
            ("node_id", nodeId.ToString()),
            ("public_key", key.PublicKeyLine),
            ("key_fingerprint", key.Fingerprint),
            ("owner_fingerprint", claimedOwnerFingerprint),
            ("machine_name", machineName),
            ("operating_system", operatingSystem),
            ("joined_at", joinedAt.ToString("o", CultureInfo.InvariantCulture)),
            ("invite_proof", inviteProof));

        // content depends only on this call's own arguments, never on what is currently on disk,
        // so a Conflict — something else wrote node.yaml between the read and the write — is
        // always safe to retry against a fresh tip. The caller above saves the local identity
        // events on the strength of this file actually landing, so a lost Conflict silently
        // treated as "nothing to write" would let that claim commit while node.yaml still held
        // the old facts.
        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
            {
                return false;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId,
                    current.Exists ? "Update node facts" : "Join node", committer, signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return true;
            }
        }

        throw new DomainConflictException(
            $"node.yaml for node {nodeId} kept changing out from under this join after "
            + $"{MaxConflictRetries} attempts — something else is writing it at the same time. "
            + "Re-run h9k project join once that settles.");
    }

    /// <summary>
    /// A small, flat YAML document: every value double-quoted (a machine name or a comment on a
    /// public key line can carry spaces or colons a bare scalar would misparse), <c>null</c>
    /// rendered as the bare YAML null literal for the reserved invite-proof field. Every field
    /// written here is a plain string or timestamp, so this is deliberately not a general YAML
    /// writer — root.yaml, node.yaml, and retired.yaml are the only three documents A2a produces.
    /// </summary>
    private static string BuildYaml(params (string Key, string? Value)[] fields)
    {
        StringBuilder builder = new();
        foreach ((string key, string? value) in fields)
        {
            builder.Append(key).Append(": ").AppendLine(value is null ? "null" : QuoteYaml(value));
        }

        return builder.ToString();
    }

    private static string QuoteYaml(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
