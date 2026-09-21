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

        [CommandOption("--from-project <NAME>")]
        [Description(
            "Names the registered project ledger this node's own key is already vouched under this "
            + "owner's root on, for the cross-project root-carry path (task f53fecfd): a node vouched "
            + "under owner root R on one project ledger joining a brand-new project ledger carries that "
            + "vouch as evidence rather than needing the root-holding node to ever touch the new "
            + "project. Omit it and every registered project this owner's node has joined is searched "
            + "for one. Only ever considered when this join names no --owner and no --invite, and only "
            + "when the target ledger has no root for R yet — refused with one plain sentence, and the "
            + "join otherwise unchanged, when no source vouch exists, this node's key is revoked on the "
            + "source, the source's own vouch predates key-bound vouches (re-run h9k node vouch there "
            + "first), or the target already has a root for R.")]
        public string? FromProject { get; init; }
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
            outcome = await RunAsync(session, project, settings.Owner, settings.Invite, settings.FromProject, cancellationToken);
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

    /// <summary>
    /// What one join produced, for both the command's own report and h9k project add's condensed
    /// one. <see cref="Deferred"/> is the one case where every other field is meaningless
    /// (<see cref="KeyFingerprint"/>, <see cref="PrivateKeyPath"/>, and
    /// <see cref="ClaimedOwnerFingerprint"/> are left blank rather than guessed at): the ledger
    /// already had a real owner and this install had no root claim of its own to bring to it, so
    /// nothing was written at all and <see cref="Report"/> has already said so and named the invite
    /// path by the time this comes back.
    /// </summary>
    internal sealed record JoinOutcome(
        Guid NodeId,
        string KeyFingerprint,
        string PrivateKeyPath,
        string ClaimedOwnerFingerprint,
        bool EstablishedRoot,
        bool RetiredPreviousRoot,
        bool WroteNodeFile,
        bool OwnerClaimChanged,
        bool CarriedRoot = false,
        string? CarriedFromProjectName = null,
        bool RootVerified = false,
        bool Deferred = false);

    /// <summary>Builds the one <see cref="JoinOutcome"/> a deferred join ever returns — every field
    /// but <see cref="JoinOutcome.NodeId"/> and <see cref="JoinOutcome.Deferred"/> left at its blank
    /// or false default rather than guessed at, since nothing was actually written.</summary>
    private static JoinOutcome DeferredOutcome(Guid nodeId) => new(
        NodeId: nodeId,
        KeyFingerprint: string.Empty,
        PrivateKeyPath: string.Empty,
        ClaimedOwnerFingerprint: string.Empty,
        EstablishedRoot: false,
        RetiredPreviousRoot: false,
        WroteNodeFile: false,
        OwnerClaimChanged: false,
        Deferred: true);

    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, CancellationToken cancellationToken) =>
        RunAsync(session, project, claimedOwnerOverride, invite: null, cancellationToken);

    /// <summary>The invite-aware overload (idea 202383dc, T2) — everything <see cref="RunAsync(IDocumentSession,ProjectDetails,string?,CancellationToken)"/>
    /// already does, plus proving possession of a minted secret when one is given. The one overload
    /// that also wires a real console prompt (task: "a newcomer who registers a project whose
    /// ledger already has an owner..."): when this process is genuinely interactive, an owner-exists
    /// deferral asks for an invite token right here rather than only naming the command to run
    /// later.</summary>
    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, string? invite,
        CancellationToken cancellationToken) =>
        RunAsync(session, project, claimedOwnerOverride, invite, fromProject: null, cancellationToken);

    /// <summary>The from-project-aware overload (task f53fecfd) — everything the invite-aware
    /// overload already does, plus every real dependency the cross-project root-carry path itself
    /// needs (<see cref="ILedgerCommitReader"/>, on top of the chain reader) and the real
    /// interactive invite-token prompt when this process is attached to a terminal. What
    /// <c>h9k project join</c>'s own <see cref="ExecuteAsync"/> calls.</summary>
    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, string? invite, string? fromProject,
        CancellationToken cancellationToken) =>
        RunAsync(
            session, project, claimedOwnerOverride, invite, fromProject,
            new GitLedger(new ConsoleWorktreeLogger<GitLedger>()), new NodeKeyStore(),
            new ProjectGitHubAccessMirror(), new GitLedgerChainReader(), new GitLedgerCommitReader(),
            AnsiConsole.Profile.Capabilities.Interactive ? PromptForInviteTokenFromConsole : null,
            cancellationToken);

    /// <summary>The real, interactive prompt <see cref="RunAsync(IDocumentSession,ProjectDetails,string?,string?,CancellationToken)"/>
    /// hands down when this process is actually attached to a terminal — never called on a
    /// non-interactive one, which passes no prompt at all rather than this. Internal rather than
    /// private so h9k project add's own join (<see cref="ProjectAddCommand.TryJoinAsync(IDocumentSession,Guid,string,string,string?,CancellationToken)"/>)
    /// can wire the identical prompt through its own call, rather than a second, private copy.</summary>
    internal static string PromptForInviteTokenFromConsole() =>
        AnsiConsole.Prompt(new TextPrompt<string>(
            "[bold]Invite token[/] [dim](paste it, or press enter to skip for now)[/]:").AllowEmpty());

    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session,
        ProjectDetails project,
        string? claimedOwnerOverride,
        ILedger ledger,
        NodeKeyStore keyStore,
        ProjectGitHubAccessMirror githubAccess,
        CancellationToken cancellationToken) =>
        RunAsync(
            session, project, claimedOwnerOverride, invite: null, fromProject: null, ledger, keyStore, githubAccess,
            chainReader: null, commitReader: null, promptForInviteToken: null, cancellationToken);

    /// <summary>The invite-aware, ledger-seamed overload with no chain reader and no interactive
    /// prompt — every pre-existing test above this piece opts out of recording the project key and
    /// of prompting this way (Brian's 2026-09-13 testing rule: this command never touches git, a
    /// network, or a real console on its own).</summary>
    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session, ProjectDetails project, string? claimedOwnerOverride, string? invite,
        ILedger ledger, NodeKeyStore keyStore, ProjectGitHubAccessMirror githubAccess,
        CancellationToken cancellationToken) =>
        RunAsync(
            session, project, claimedOwnerOverride, invite, fromProject: null, ledger, keyStore, githubAccess,
            chainReader: null, commitReader: null, promptForInviteToken: null, cancellationToken);

    /// <summary>The invite- and chain-reader-aware overload — every pre-existing test above this
    /// piece that opts into project-key recording, never the carry path: no test above this piece
    /// drives that against real git either (Brian's 2026-09-13 testing rule), so the carry path's
    /// own tests live in <c>GitLedgerChainReaderTests</c> instead, on the fake ledger the chain
    /// reader tests already use.</summary>
    internal static Task<JoinOutcome> RunAsync(
        IDocumentSession session,
        ProjectDetails project,
        string? claimedOwnerOverride,
        string? invite,
        ILedger ledger,
        NodeKeyStore keyStore,
        ProjectGitHubAccessMirror githubAccess,
        ILedgerChainReader? chainReader,
        CancellationToken cancellationToken) =>
        RunAsync(
            session, project, claimedOwnerOverride, invite, fromProject: null, ledger, keyStore, githubAccess,
            chainReader, commitReader: null, promptForInviteToken: null, cancellationToken);

    /// <summary>
    /// The whole join flow, seamed on <see cref="ILedger"/>, <see cref="NodeKeyStore"/>, and
    /// <see cref="ProjectGitHubAccessMirror"/> so a test drives it against the in-memory ledger
    /// fake, a stubbed key generator, and a fake gh transport rather than a real repository, a real
    /// ssh-keygen invocation, or a real gh/network call (Brian's 2026-09-13 testing rule).
    /// <paramref name="promptForInviteToken"/> is the identical seam for the one interactive prompt
    /// this flow can make: null means never ask (a script, an agent, or any other non-interactive
    /// caller), and a real delegate is called at most once, when this install has no root claim of
    /// its own and the project's ledger already has a real owner — its return value is the token
    /// pasted, or blank/null for "pressed enter, skip for now".
    /// </summary>
    internal static async Task<JoinOutcome> RunAsync(
        IDocumentSession session,
        ProjectDetails project,
        string? claimedOwnerOverride,
        string? invite,
        string? fromProject,
        ILedger ledger,
        NodeKeyStore keyStore,
        ProjectGitHubAccessMirror githubAccess,
        ILedgerChainReader? chainReader,
        ILedgerCommitReader? commitReader,
        Func<string?>? promptForInviteToken,
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

        // The no-owner, no-invite path's own gate (task: "a newcomer who registers a project whose
        // ledger already has an owner is told so and asked for their invite token instead of being
        // minted as a second owner root"). Checked here, before any key is generated or any ledger
        // byte is written. Neither --owner nor --invite says who this node belongs to, so the only
        // question left is whether this join's own eventual fallback — claimedFingerprint below
        // ending up equal to this node's own key — would self-establish a root in a project whose
        // ledger already answered that question before this join ever ran.
        //
        // That fallback fires in two cases, not only the first: owner.RootFingerprint is still
        // null (a genuinely fresh install, never claimed any root at all), or it already equals
        // this node's own key because an earlier join of a *different* project already established
        // it there (independent pre-PR review, cycle 1, adversarial lens, medium — a gate that only
        // checked the first case let a node that already owns one project self-write a second
        // owner's root.yaml and node.yaml into a project it was only ever meant to be invited
        // into). This node's own fingerprint is read from the already-loaded node aggregate with
        // no I/O — NodeKeyStore.Fingerprint is a pure hash over node.PublicKey — rather than minted
        // or read from disk here, preserving the "no key touched before this gate decides" promise
        // for the still-fresh-install case, where node.PublicKey is null and this comparison can
        // never match.
        //
        // An explicit --owner or --invite always skips this: both already name (or, for a
        // member-of-project invite, deliberately leave open) whose root this join claims, so there
        // is nothing here for this gate to defer.
        string? thisNodesOwnFingerprint = node.PublicKey is not null ? NodeKeyStore.Fingerprint(node.PublicKey) : null;
        bool wouldSelfEstablish = owner.RootFingerprint is null || owner.RootFingerprint == thisNodesOwnFingerprint;
        if (claimedOwnerOverride.IsBlank() && invite.IsBlank() && wouldSelfEstablish)
        {
            GenesisDeferralOutcome deferral = await CheckGenesisDeferralAsync(
                session, project, ledger, owner.RootFingerprint, promptForInviteToken, cancellationToken);
            if (deferral.Defer)
            {
                return DeferredOutcome(context.NodeId);
            }

            if (deferral.InviteToken is not null)
            {
                // A token was pasted on the spot — falls through into the ordinary --invite path
                // below in this same call, exactly as h9k project join <project> --invite <token>
                // run by hand would.
                invite = deferral.InviteToken;
            }
        }

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

        // Captured once, before the ClaimRoot append below can change what this call means by
        // "the owner's root": the in-memory owner aggregate loaded above is never re-aggregated
        // from this session's own pending events, so every later read of owner.RootFingerprint in
        // this method still sees whichever root was claimed BEFORE this join ran, even after a
        // ClaimRoot for a different one has already been appended into this same session
        // (independent pre-PR review, adversarial lens, medium — the root-verification
        // reconciliation below must never reason about "the owner's current root" using that stale
        // value once this is true).
        bool claimedRootChanged = owner.RootFingerprint != claimedFingerprint;

        bool retiredPreviousRoot = false;
        if (claimedRootChanged)
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

        // Set only when this exact call just minted this project's own key writing the genesis
        // members commit (idea 202383dc, M2) — the one case where the value is already known
        // in hand and re-reading the ledger for it would be redundant. Left null otherwise
        // (an ordinary join, an invite join, or a lost genesis race), so RecordProjectKeyAsync
        // below reads it back from the ledger instead.
        string? justWrittenProjectKey = null;

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
            //
            // Never reached when this join carries an invite (idea 202383dc, T2 criterion 3): a
            // join with an invite is by definition not this project's first member — someone
            // already enrolled had to mint the secret it carries — so it must never self-claim
            // genesis, whatever the members folder's own state happens to be (a project that
            // predates the chain leaves it empty until its own owner's plain join lands). This
            // node's own membership comes only from the minting node's own sweep vouch, once its
            // proof appears in its node file below.
            if (invite.IsBlank())
            {
                (bool wroteGenesisMember, string genesisProjectKey) = await EnsureGenesisMemberFileAsync(
                    ledger, project.RepositoryPath, claimedFingerprint, now, committer, signingKey, cancellationToken);
                if (wroteGenesisMember)
                {
                    session.Events.Append(
                        project.Id, ProjectDecider.VouchMember(project.Id, claimedFingerprint, ProjectMemberRole.Owner, now));
                    justWrittenProjectKey = genesisProjectKey;
                }
            }
        }

        // The cross-project root-carry path (task f53fecfd): only ever considered when this join
        // names no --owner and no --invite (claimedFingerprint already comes from owner.RootFingerprint,
        // not a fresh claim) and is not establishing this node's own key as a brand-new root. Both
        // seams are optional through every pre-existing overload (Brian's 2026-09-13 testing rule),
        // so an ordinary test that opts out of them never attempts this at all.
        bool carriedRoot = false;
        string? carriedFromProjectName = null;
        if (!establishingRoot && claimedOwnerOverride.IsBlank() && invite.IsBlank() && chainReader is not null && commitReader is not null)
        {
            CarryAttemptOutcome carryOutcome = await TryCarryVouchAsync(
                session, ledger, chainReader, commitReader, project, context.OwnerId, context.NodeId, key,
                claimedFingerprint, fromProject, committer, signingKey, now, cancellationToken);
            if (carryOutcome.Carried)
            {
                carriedRoot = true;
                carriedFromProjectName = carryOutcome.SourceProjectName;

                // Verified immediately, the same call establishingRoot's own ClaimRoot(verified:
                // true) makes for a self-established root — carrying in is itself the evidence, so
                // there is nothing to wait on a later chain read for. The ordinary ClaimRoot append
                // above never fires here (owner.RootFingerprint already equals claimedFingerprint
                // by construction whenever this branch runs), so without this the persisted
                // RootFingerprintVerified would stay false until the next reconciliation happened
                // to run, even though this join's own report already says "verified" (independent
                // pre-PR review, adversarial lens, medium).
                if (!owner.RootFingerprintVerified)
                {
                    session.Events.Append(context.OwnerId, OwnerDecider.VerifyRoot(owner, now));
                }

                (bool wroteGenesisMember, string genesisProjectKey) = await EnsureGenesisMemberFileAsync(
                    ledger, project.RepositoryPath, claimedFingerprint, now, committer, signingKey, cancellationToken);
                if (wroteGenesisMember)
                {
                    session.Events.Append(
                        project.Id, ProjectDecider.VouchMember(project.Id, claimedFingerprint, ProjectMemberRole.Owner, now));
                    justWrittenProjectKey = genesisProjectKey;
                }
            }
            else if (carryOutcome.RefusalMessage is not null)
            {
                AnsiConsole.MarkupLine($"[yellow]{carryOutcome.RefusalMessage.EscapeMarkup()}[/]");
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

        // Recorded so a later brand-new bootstrap (MessageSweepEngine.ResolveVoucherNodeId) can name
        // the inviting owner as its own voucher tier — the ranking EventCatchUpCoordinator.RankCandidates
        // already implements but which was otherwise unreachable in production: a brand-new node is
        // brand-new precisely because it just joined on someone's invite, and this is the one place
        // that invite's own minting owner is ever in hand (independent pre-PR review, cycle 1,
        // conformance lens, medium).
        if (inviteMinterRoot is not null && node.InviterOwnerRootFingerprint != inviteMinterRoot)
        {
            session.Events.Append(context.NodeId, NodeDecider.RecordInviter(node, inviteMinterRoot, now));
        }

        await RecordProjectKeyAsync(session, project, justWrittenProjectKey, chainReader, now, cancellationToken);

        // Reconciles OwnerAggregate.RootFingerprintVerified from a live chain read (task f53fecfd,
        // criterion 4 — the gap draft f245371d found): establishingRoot and carriedRoot both already
        // recorded verified: true directly above, so this only ever has real work to do for the
        // "claimed elsewhere with --owner, still unverified" case — an owner later vouched into that
        // root on the ledger by someone else, or by a carried record, otherwise stays "claimed,
        // unverified" in h9k owner show and h9k status forever even though h9k project members
        // already reads the identical live chain and shows it verified. Best-effort and never fatal
        // to the join: a transient chain-read failure here simply leaves nothing to reconcile this
        // tick, same as RecordProjectKeyAsync's own failure handling just above.
        //
        // Never run when this same call just changed which root the owner claims (claimedRootChanged):
        // OwnerRootVerificationReconciler.Reconcile reasons entirely from the in-memory owner
        // aggregate above, which still holds the PREVIOUS root's own fingerprint (this method never
        // re-aggregates it after the ClaimRoot append). Reconciling against that stale value here
        // would check enrollment under the wrong root and — because OwnerRootVerified carries no
        // fingerprint of its own — mark whatever root this call just claimed as verified even though
        // nothing under it was ever actually vouched (independent pre-PR review, adversarial lens,
        // medium). Skipping costs nothing but a tick: the daemon's own message sweep reconciles the
        // freshly claimed root correctly on its next pass, once a fresh aggregate load sees it.
        bool rootVerifiedByReconciliation = false;
        if (!establishingRoot && !carriedRoot && !claimedRootChanged && chainReader is not null)
        {
            try
            {
                TrustChain reconciliationChain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
                rootVerifiedByReconciliation =
                    OwnerRootVerificationReconciler.Reconcile(session, owner, key.Fingerprint, reconciliationChain, now);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Nothing to reconcile from an unreadable chain this tick; a later join or the
                // daemon's own message sweep tries again.
            }
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
                    session, context.NodeId, project.Id, claimedFingerprint, MessageAudience.Owner(inviteMinterRoot),
                    about: null, MessageKind.Note, $"Invite proof written for node {context.NodeId} in '{project.Name}'.",
                    now, cancellationToken);
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

        // The same staleness claimedRootChanged already guards the reconciliation call against
        // applies here too: owner.RootFingerprintVerified is only trustworthy for THIS report when
        // this call never changed which root is claimed. When it did, that flag still describes the
        // PREVIOUS claim (a self-established or already-vouched root can easily have been verified
        // before this join ever ran), and reporting it here would tell the operator the brand-new
        // claim is "verified" when nothing under it has ever actually been vouched (independent
        // pre-PR review sweep, same shape as the adversarial lens's own finding on the reconciliation
        // call above).
        bool rootVerified = establishingRoot || carriedRoot || rootVerifiedByReconciliation
            || (!claimedRootChanged && owner.RootFingerprintVerified);
        return new JoinOutcome(
            context.NodeId, key.Fingerprint, key.PrivateKeyPath, claimedFingerprint,
            establishingRoot, retiredPreviousRoot, wroteNodeFile, ownerClaimChanged,
            carriedRoot, carriedFromProjectName,
            RootVerified: rootVerified);
    }

    internal static void Report(ProjectDetails project, JoinOutcome outcome)
    {
        if (outcome.Deferred)
        {
            // CheckGenesisDeferralAsync already said everything there is to say, at the moment the
            // decision was made: who the existing owner is, and either the invite path this same
            // call then ran, or the exact command to run once a token is in hand. Nothing further
            // to report against a join that wrote nothing.
            return;
        }

        AnsiConsole.MarkupLine(
            $"[green]Joined '{project.Name.EscapeMarkup()}'.[/] Node [dim]{outcome.NodeId}[/], "
            + $"key [dim]{outcome.KeyFingerprint}[/] at [dim]{outcome.PrivateKeyPath.EscapeMarkup()}[/].");
        AnsiConsole.MarkupLine(outcome switch
        {
            { EstablishedRoot: true } =>
                $"[dim]This is this owner's root — {outcome.ClaimedOwnerFingerprint} is the owner id everywhere in Hall9k now.[/]",
            { CarriedRoot: true } =>
                $"[dim]Carried this owner's root in from '{outcome.CarriedFromProjectName.EscapeMarkup()}' — "
                + $"{outcome.ClaimedOwnerFingerprint} is verified here on this node's own existing vouch, with no "
                + "need for the root-holding node to ever touch this project.[/]",
            { RootVerified: true } =>
                $"[dim]Claimed owner {outcome.ClaimedOwnerFingerprint}, verified.[/]",
            _ =>
                $"[dim]Claimed owner {outcome.ClaimedOwnerFingerprint}, unverified until an already-enrolled node of that owner confirms it (h9k node vouch, or a matched h9k node invite).[/]",
        });
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

    /// <summary>What <see cref="CheckGenesisDeferralAsync"/> decided: <see cref="Defer"/> means the
    /// caller writes nothing and returns <see cref="DeferredOutcome"/> — nothing was pasted, whether
    /// because this process never asked (non-interactive) or because the operator pressed enter.
    /// <see cref="InviteToken"/> is the one case that is neither: a real token was pasted on the
    /// spot, for the caller to fall through into the ordinary --invite path with.</summary>
    private readonly record struct GenesisDeferralOutcome(bool Defer, string? InviteToken);

    /// <summary>
    /// The no-owner, no-invite gate's own decision (task: "a newcomer who registers a project whose
    /// ledger already has an owner..."), split out of <see cref="RunAsync(IDocumentSession,ProjectDetails,string?,string?,ILedger,NodeKeyStore,ProjectGitHubAccessMirror,ILedgerChainReader?,Func{string?}?,CancellationToken)"/>
    /// so its own early returns read as one decision rather than a nest of them inline. Checked
    /// against the ledger's own <c>members/</c> folder — <see cref="ILedger.HasAnyAsync"/>, the
    /// identical existence check <see cref="EnsureGenesisMemberFileAsync"/> already trusts for
    /// "has this project's genesis already been spent" — never against <c>owner.RootFingerprint</c>
    /// alone, which says nothing about whether the ledger itself still has genesis up for grabs.
    /// <paramref name="selfFingerprint"/> is the fingerprint this join would self-establish under
    /// (null on a genuinely fresh install with no root of its own yet); when the ledger's own
    /// genesis already names that exact fingerprint, this is a re-join of a project this same
    /// identity already owns — not a newcomer meeting someone else's project — so this defers to
    /// nobody and lets the ordinary establishing-root path below run unchanged.
    /// </summary>
    private static async Task<GenesisDeferralOutcome> CheckGenesisDeferralAsync(
        IDocumentSession session, ProjectDetails project, ILedger ledger, string? selfFingerprint,
        Func<string?>? promptForInviteToken, CancellationToken cancellationToken)
    {
        bool alreadyHasGenesis = await ledger.HasAnyAsync(project.RepositoryPath, MembersRefName, "members/", cancellationToken);
        if (!alreadyHasGenesis)
        {
            // An empty ledger — this is genuinely this project's first join, and the ordinary
            // establishing-root path below is exactly right for it, unchanged.
            return new GenesisDeferralOutcome(Defer: false, InviteToken: null);
        }

        string? genesisOwnerFingerprint = await FindGenesisOwnerFingerprintAsync(ledger, project.RepositoryPath, cancellationToken);
        if (genesisOwnerFingerprint is not null && genesisOwnerFingerprint == selfFingerprint)
        {
            // This project's own genesis already belongs to this exact identity — a re-join
            // (a fresh checkout, a second registration of a repository this node already owns),
            // never the second-owner hazard this gate exists to catch. EnsureGenesisMemberFileAsync
            // below is already a no-op against an entry that is already there under this same
            // fingerprint.
            return new GenesisDeferralOutcome(Defer: false, InviteToken: null);
        }

        string ownerDisplay = genesisOwnerFingerprint is not null
            ? await ResolveOwnerDisplayAsync(session, genesisOwnerFingerprint, cancellationToken)
            : "another owner this install does not recognize";

        AnsiConsole.MarkupLine(
            $"[yellow]'{project.Name.EscapeMarkup()}' already has an owner ({ownerDisplay.EscapeMarkup()}), and "
            + "joining needs an invite from them rather than a fresh root of your own.[/]");

        string? token = promptForInviteToken?.Invoke();
        if (token.IsNotBlank())
        {
            return new GenesisDeferralOutcome(Defer: false, InviteToken: token);
        }

        AnsiConsole.MarkupLine(
            $"[dim]Skipped for now — the registration stays. Once you have a token: h9k project join "
            + $"{project.Name.EscapeMarkup()} --invite <token>[/]");
        return new GenesisDeferralOutcome(Defer: true, InviteToken: null);
    }

    /// <summary>
    /// The root fingerprint behind whichever <c>members/*.yaml</c> entry actually carries the owner
    /// role — genesis's own rule (idea 202383dc, T1) says there is at most one, the project's first
    /// member — falling back to the first entry found at all when none is role owner (a members
    /// folder this reader cannot make full sense of still names someone rather than no one). This is
    /// deliberately unverified, raw ledger content: the decision this feeds
    /// (<see cref="CheckGenesisDeferralAsync"/>'s own write-nothing gate) is already made from
    /// <see cref="ILedger.HasAnyAsync"/> alone, so a forged or malformed entry here can only ever
    /// affect which name shows up in a friendly heads-up message, never whether this join writes
    /// anything.
    /// </summary>
    private static async Task<string?> FindGenesisOwnerFingerprintAsync(
        ILedger ledger, string repositoryPath, CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> entries = await ledger.ReadAllAsync(repositoryPath, MembersRefName, "members/", cancellationToken);
        foreach (LedgerEntry entry in entries)
        {
            if (ExtractQuotedYamlValue(entry.Content, "role") == ProjectMemberRole.Owner.Value)
            {
                return ExtractQuotedYamlValue(entry.Content, "root_fingerprint");
            }
        }

        return entries.Count > 0 ? ExtractQuotedYamlValue(entries[0].Content, "root_fingerprint") : null;
    }

    /// <summary>The existing owner's own login when this install's local Owner documents happen to
    /// know one for that fingerprint (the same local lookup <c>h9k project members</c>'s own table
    /// already does for its Login column), else the bare fingerprint — never guessed at.</summary>
    private static async Task<string> ResolveOwnerDisplayAsync(
        IDocumentSession session, string genesisOwnerFingerprint, CancellationToken cancellationToken)
    {
        OwnerDetails? localOwner = await session.Query<OwnerDetails>()
            .Where(candidate => candidate.RootFingerprint == genesisOwnerFingerprint)
            .FirstOrDefaultAsync(cancellationToken);
        return localOwner is not null && localOwner.Name.IsNotBlank() ? localOwner.Name : genesisOwnerFingerprint;
    }

    /// <summary>Mirrors <c>GitLedgerChainReader.ExtractQuotedYamlValue</c>'s own small, flat reader —
    /// duplicated here rather than shared across the seam, the same choice <c>InviteSweepEngine</c>'s
    /// own identical copy already made.</summary>
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
    /// <para>
    /// Also mints this project's own key fresh (idea 202383dc, M2, Brian's ruling 2026-09-17: a
    /// generated id, never the genesis owner's fingerprint) — a 26-character ULID from Cysharp's
    /// Ulid, chosen for speed; every other id in this codebase stays a DomainId UUIDv7 — and
    /// records it as this genesis commit's own <c>project_key</c> field. Returned regardless of
    /// whether this call actually won the write, so a caller who lost the race can fall back to
    /// reading the winner's own key back from the ledger instead.
    /// </para>
    /// </summary>
    private static async Task<(bool Wrote, string ProjectKey)> EnsureGenesisMemberFileAsync(
        ILedger ledger, string repositoryPath, string fingerprint, DateTimeOffset issuedAt,
        LedgerCommitter committer, LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        string path = $"members/{fingerprint}.yaml";
        string projectKey = Ulid.NewUlid().ToString();

        if (await ledger.HasAnyAsync(repositoryPath, MembersRefName, "members/", cancellationToken))
        {
            // Genesis was already spent — by this fingerprint's own earlier join, or by someone
            // else's — so this join is not this project's first member and must not self-claim
            // ownership. A re-run whose own file is already there also lands here, correctly, as a
            // no-op: HasAnyAsync is true either way. A cheap early exit only — the write below is
            // what actually enforces this, against a tip fetched fresh in the same attempt.
            return (false, projectKey);
        }

        string content = BuildYaml(
            ("root_fingerprint", fingerprint),
            ("role", ProjectMemberRole.Owner.Value),
            ("issued_at", issuedAt.ToString("o", CultureInfo.InvariantCulture)),
            ("project_key", projectKey));

        // RequireEmptyPrefix, not just ExpectedBlobId, closes the race the check above cannot: two
        // joins racing to establish genesis under two different fingerprints would each see
        // members/ empty here and both proceed, since ExpectedBlobId alone only guards this write's
        // own path. The prefix is re-checked against the ref's own freshly-fetched tip inside
        // WriteAsync's own retry loop, so whichever push actually lands first is the only one that
        // can win (independent review finding).
        LedgerWriteOutcome outcome = await ledger.WriteAsync(
            new LedgerWriteRequest(
                repositoryPath, MembersRefName, path, content, ExpectedBlobId: null,
                $"Establish {fingerprint} as this project's first owner-role member", committer, signingKey,
                RequireEmptyPrefix: "members/"),
            cancellationToken);

        // A conflict here means another join won the race to establish the identical genesis
        // member between the read above and this write — the entry exists either way, under
        // whichever caller's own freshly-minted key actually won.
        return (outcome.Verdict == LedgerWriteVerdict.Written, projectKey);
    }

    /// <summary>What one carry attempt produced — either it carried, or it did not and names why
    /// (null when the "why" is simply "not applicable, say nothing" — the everyday steady state for
    /// a project this node already established a root in some other way).</summary>
    private sealed record CarryAttemptOutcome(bool Carried, string? SourceProjectName, string? RefusalMessage)
    {
        public static readonly CarryAttemptOutcome NotApplicable = new(false, null, null);
    }

    /// <summary>
    /// The cross-project root-carry path (task f53fecfd): when this node's own key is already
    /// vouched under owner root <paramref name="root"/> on some OTHER registered project ledger, and
    /// <paramref name="project"/>'s own ledger has no root for it yet, carries that vouch in as
    /// evidence — <c>owners/&lt;root&gt;/root.yaml</c> (a verbatim copy of the source's own) and
    /// <c>owners/&lt;root&gt;/carried/&lt;node-id&gt;.yaml</c> (the evidence bundle
    /// <see cref="GitLedgerChainReader"/> verifies offline: the source root.yaml and vouch file, the
    /// raw bytes of both signed commits, and provenance), in one push — rather than requiring the
    /// root-holding node to ever touch this project.
    /// <para>
    /// Silent (<see cref="CarryAttemptOutcome.RefusalMessage"/> null) when the target already has a
    /// root and no <paramref name="fromProjectName"/> was named: the everyday steady state for a
    /// project this node has already joined some other way, not worth a line of console noise on
    /// every ordinary re-join ("a quiet pane says nothing", <c>ProjectMembersCommand</c>'s own
    /// convention). Every other refusal reason — no source vouch found, this node's key revoked on
    /// the source, or an explicit <c>--from-project</c> naming a target that already has a root or a
    /// source with nothing to carry — gets the one plain sentence the task's own acceptance
    /// criterion asks for, and either way today's other branches apply unchanged.
    /// </para>
    /// </summary>
    private static async Task<CarryAttemptOutcome> TryCarryVouchAsync(
        IDocumentSession session, ILedger ledger, ILedgerChainReader chainReader, ILedgerCommitReader commitReader,
        ProjectDetails project, Guid ownerId, Guid nodeId, NodeSigningKey key, string root, string? fromProjectName,
        LedgerCommitter committer, LedgerSigningKey signingKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string ownersRefName = $"refs/hall9k/ledger/owners/{root}";
        string rootPath = $"owners/{root}/root.yaml";
        LedgerFile existingTargetRoot = await ledger.ReadAsync(project.RepositoryPath, ownersRefName, rootPath, cancellationToken);
        if (existingTargetRoot.Exists)
        {
            return fromProjectName.IsBlank()
                ? CarryAttemptOutcome.NotApplicable
                : new CarryAttemptOutcome(
                    false, null, $"'{project.Name}' already has a root for {root} — nothing to carry from '{fromProjectName}'.");
        }

        IReadOnlyList<ProjectDetails> candidates;
        if (fromProjectName.IsNotBlank())
        {
            ProjectDetails named;
            try
            {
                named = ProjectResolver.Match(
                    await session.Query<ProjectDetails>().Where(candidate => !candidate.IsArchived).ToListAsync(cancellationToken),
                    fromProjectName);
            }
            catch (Exception exception) when (exception is DomainNotFoundException or DomainConflictException)
            {
                return new CarryAttemptOutcome(false, null, $"--from-project {fromProjectName} does not name a registered project: {exception.Message}");
            }

            if (named.Id == project.Id)
            {
                return new CarryAttemptOutcome(false, null, $"--from-project cannot name the project being joined ('{project.Name}').");
            }

            candidates = [named];
        }
        else
        {
            candidates = await session.Query<ProjectDetails>()
                .Where(candidate => candidate.OwnerId == ownerId && candidate.Id != project.Id && !candidate.IsArchived)
                .ToListAsync(cancellationToken);
        }

        bool revokedFound = false;
        bool sourceRootNotRootSignedFound = false;
        bool delegateSignedVouchFound = false;
        bool oldFormatVouchFound = false;
        foreach (ProjectDetails source in candidates)
        {
            TrustChain sourceChain;
            try
            {
                sourceChain = await chainReader.ComputeAsync(source.RepositoryPath, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                if (fromProjectName.IsNotBlank())
                {
                    return new CarryAttemptOutcome(
                        false, null, $"Could not read '{source.Name}'s own ledger to carry a vouch from it: {exception.Message}");
                }

                continue;
            }

            if (!sourceChain.OwnerChains.TryGetValue(root, out TrustedOwner? sourceOwner))
            {
                continue;
            }

            if (sourceOwner.RevokedNodeIds.Contains(nodeId.ToString()))
            {
                revokedFound = true;
                continue;
            }

            TrustedNode? activeNode = sourceOwner.Nodes.FirstOrDefault(
                candidate => candidate.NodeId == nodeId.ToString() && candidate.Fingerprint == key.Fingerprint);
            if (activeNode is null)
            {
                continue;
            }

            string sourceVouchPath = $"owners/{root}/nodes/{nodeId}.yaml";
            LedgerSignedCommit? rootSigned = await commitReader.ReadSignedCommitAsync(
                source.RepositoryPath, ownersRefName, rootPath, cancellationToken);
            LedgerSignedCommit? vouchSigned = await commitReader.ReadSignedCommitAsync(
                source.RepositoryPath, ownersRefName, sourceVouchPath, cancellationToken);
            if (rootSigned is null || vouchSigned is null)
            {
                // The chain read above already confirmed this node is currently vouched there — an
                // unreadable commit here means something changed between those two reads (a
                // concurrent revocation, an unreachable remote mid-attempt). Try the next candidate
                // rather than fail the whole join over a race this node did not cause.
                continue;
            }

            // Mirrors GitLedgerChainReader.VerifyCarriedRecordAsync's own check 2: the embedded root
            // commit has to be signed by the root's own key. A source project whose own root.yaml
            // was itself established by an earlier carry (never self-signed by root R, only by
            // whichever node carried it there) fails this — carrying that commit's own bytes verbatim
            // into the target would write owners/R/root.yaml there with no signature this or any
            // later reader can ever trust, since a carried record's own embedded root commit is the
            // one thing check 2 always demands be root-signed regardless of how the source itself
            // came to hold root R. That would permanently burn the target's own root.yaml slot —
            // nothing can overwrite it once it exists, established or not — for exactly the same
            // reason a delegate-signed or pre-key-binding vouch is skipped just below (independent
            // pre-PR review, adversarial lens, medium). Skipped rather than failed outright: another
            // candidate project, or a source whose own root.yaml really is root-signed, may still
            // carry cleanly.
            if (!await commitReader.IsSignedByAsync(
                source.RepositoryPath, rootSigned.RawCommitBytes, sourceOwner.RootPublicKeyLine, cancellationToken))
            {
                sourceRootNotRootSignedFound = true;
                continue;
            }

            // Pre-verified against the identical single-hop rule GitLedgerChainReader's own
            // VerifyCarriedRecordAsync will apply on every later read (signed directly by the
            // root's own key — never merely by some other node the source ledger's own, more
            // permissive "root or any enrolled node" rule accepted for an ordinary vouch there,
            // NodeVouchCommand's own doc: "written by any enrolled node of that owner"). Without
            // this, a delegate-signed vouch would carry cleanly here and then never verify on any
            // future read anywhere, permanently burning this project's own root.yaml slot — nothing
            // can overwrite it once it exists, established or not (independent pre-PR review,
            // adversarial lens, high). Skipped rather than failed outright: another candidate
            // project, or a source root the target project's own root holder directly enrolled this
            // node under, may still carry cleanly.
            if (!await commitReader.IsSignedByAsync(
                source.RepositoryPath, vouchSigned.RawCommitBytes, sourceOwner.RootPublicKeyLine, cancellationToken))
            {
                delegateSignedVouchFound = true;
                continue;
            }

            // Mirrors GitLedgerChainReader.VerifyCarriedRecordAsync's own check 3 exactly: the
            // reader accepts a carried vouch only when its signed commit message names both this
            // node id AND the fingerprint of the exact key it carries. A vouch written before that
            // binding shipped ("Vouch node {id}", or "Vouch node {id} (invite)" — every vouch
            // NodeVouchCommand or InviteSweepEngine wrote before this branch) will never satisfy it.
            // Carrying one in anyway would write a bundle no later read can ever verify, permanently
            // burning this project's own root.yaml slot the identical way a delegate-signed vouch
            // would above (independent pre-PR review, cycle 3, both lenses, high). Skipped rather
            // than failed outright, for the same reason as every other check in this loop: another
            // candidate project may hold a fresh, key-bound vouch for this node.
            string vouchMarker = $"Vouch node {nodeId} key {key.Fingerprint}";
            if (!vouchSigned.RawCommitBytes.Contains(vouchMarker, StringComparison.OrdinalIgnoreCase))
            {
                oldFormatVouchFound = true;
                continue;
            }

            string carriedContent = BuildCarriedRecordYaml(
                nodeId, key.PublicKeyLine, source.Id, source.ProjectKey, source.RepositoryUrl?.ToString() ?? source.RepositoryPath,
                rootSigned, vouchSigned, now);

            LedgerWriteOutcome outcome = await ledger.WriteManyAsync(
                new LedgerManyWriteRequest(
                    project.RepositoryPath, ownersRefName,
                    [
                        new LedgerFileWrite(rootPath, rootSigned.Content, ExpectedBlobId: null),
                        new LedgerFileWrite($"owners/{root}/carried/{nodeId}.yaml", carriedContent, ExpectedBlobId: null),
                    ],
                    $"Carry node {nodeId}'s own vouch under root {root} in from '{source.Name}'", committer, signingKey),
                cancellationToken);

            if (outcome.Verdict != LedgerWriteVerdict.Written)
            {
                // Lost the race to establish this root — another node (or another join attempt)
                // landed one between the read at the top of this method and this write.
                return new CarryAttemptOutcome(
                    false, null, $"'{project.Name}' already has a root for {root} — nothing to carry from '{source.Name}'.");
            }

            return new CarryAttemptOutcome(true, source.Name, null);
        }

        return new CarryAttemptOutcome(
            false, null,
            revokedFound
                ? $"This node's key is revoked under owner {root} on the source ledger — nothing carried in."
                : sourceRootNotRootSignedFound
                    ? $"The source project's own root {root} was not established directly by the root's own "
                        + "key (it was itself carried in from elsewhere) and cannot be carried again from "
                        + "there — join that source project's own ledger first with the root-holding node, "
                        + "then retry h9k project join."
                    : delegateSignedVouchFound
                        ? $"This node's vouch under owner {root} on the source ledger was written by a "
                            + "delegate node rather than the root's own key and cannot be carried — ask the "
                            + "root-holding node to run h9k node vouch for this node directly, then retry "
                            + "h9k project join."
                        : oldFormatVouchFound
                            ? $"This node's vouch under owner {root} predates key-bound vouches and cannot be carried "
                                + "in safely — re-run h9k node vouch for this node on the source project first, then "
                                + "retry h9k project join."
                            : "No source vouch was found for this node's key under owner "
                                + $"{root} on any registered project ledger"
                                + (fromProjectName.IsNotBlank() ? $" named '{fromProjectName}'" : string.Empty)
                                + " — nothing carried in.");
    }

    /// <summary>
    /// A carried bundle's own small, flat document (task f53fecfd, criterion 1): the carried node's
    /// own key, source provenance, and the source root.yaml/vouch file content plus the raw bytes of
    /// both signed commits — every multi-line value base64-encoded so it still fits this class' own
    /// single-line-per-field <see cref="BuildYaml"/> shape unchanged. <see cref="GitLedgerChainReader"/>'s
    /// own <c>VerifyCarriedRecordAsync</c> is this method's exact mirror on the read side.
    /// </summary>
    private static string BuildCarriedRecordYaml(
        Guid nodeId, string nodePublicKeyLine, Guid sourceProjectId, string? sourceProjectKey, string sourceOriginUrl,
        LedgerSignedCommit rootSigned, LedgerSignedCommit vouchSigned, DateTimeOffset carriedAt) =>
        BuildYaml(
            ("node_id", nodeId.ToString()),
            ("node_public_key", nodePublicKeyLine),
            ("source_project_id", sourceProjectId.ToString()),
            ("source_project_key", sourceProjectKey),
            ("source_origin_url", sourceOriginUrl),
            ("root_yaml_base64", EncodeBase64(rootSigned.Content)),
            ("root_commit_sha", rootSigned.CommitSha),
            ("root_commit_base64", EncodeBase64(rootSigned.RawCommitBytes)),
            ("vouch_yaml_base64", EncodeBase64(vouchSigned.Content)),
            ("vouch_commit_sha", vouchSigned.CommitSha),
            ("vouch_commit_base64", EncodeBase64(vouchSigned.RawCommitBytes)),
            ("carried_at", carriedAt.ToString("o", CultureInfo.InvariantCulture)));

    private static string EncodeBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Records this install's own local mirror of the project's key (idea 202383dc, M2): the value
    /// this exact call just minted writing genesis (<paramref name="justWrittenProjectKey"/>), or —
    /// every other join, when a caller actually handed this a real <paramref name="chainReader"/> —
    /// whatever it reads back from the ledger right now. Best-effort and never fatal to the join:
    /// this is a convenience record (<c>h9k project show</c>, a safety net for envelope resolution),
    /// never a requirement the join itself depends on — a null <paramref name="chainReader"/> (an
    /// innermost-overload caller that opted out of it, every pre-existing test above this piece
    /// among them — this class's own doc: this command never touches git or a network on its own), a
    /// ledger whose genesis predates this piece with no key yet (<c>h9k project assign-key</c> never
    /// ran), or a transient chain-read failure all simply leave nothing to record here, and a later
    /// join or assign-key picks it up.
    /// </summary>
    private static async Task RecordProjectKeyAsync(
        IDocumentSession session, ProjectDetails project, string? justWrittenProjectKey,
        ILedgerChainReader? chainReader, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? resolvedProjectKey = justWrittenProjectKey;
        if (resolvedProjectKey is null && chainReader is not null)
        {
            try
            {
                TrustChain trustChain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
                resolvedProjectKey = trustChain.ProjectKey;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return;
            }
        }

        if (resolvedProjectKey is null || project.ProjectKey == resolvedProjectKey)
        {
            return;
        }

        try
        {
            session.Events.Append(project.Id, ProjectDecider.AssignKey(project.Id, resolvedProjectKey, now));
        }
        catch (DomainValidationException)
        {
            // A key this call just minted itself (justWrittenProjectKey) is always well-formed and
            // can never reach here — only a value read back from the ledger can be malformed (a
            // corrupt or hand-edited project_key field), and a bad shape on that convenience record
            // must never abort a join whose own node-file write and owner claim already succeeded
            // above, the identical "best-effort, never fatal" promise this method's own doc makes.
        }
    }

    /// <summary>How many times a conflicting ledger write retries against a fresh read before giving up.</summary>
    private const int MaxConflictRetries = 5;

    /// <summary>The one members ref every project's genesis and membership live under — shared so
    /// the no-owner deferral gate (<see cref="CheckGenesisDeferralAsync"/>) and the genesis write
    /// itself (<see cref="EnsureGenesisMemberFileAsync"/>) can never name it two different ways.</summary>
    private const string MembersRefName = "refs/hall9k/ledger/members";

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
