using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Trust;

/// <summary>A project member's role — owner or member, idea 202383dc's own closed pair (PLAN.md
/// bearings: "Roles are owner and member only").</summary>
public enum MembershipRole
{
    Owner,
    Member,
}

/// <summary>One node vouched into an owner's fleet — currently active (not the latest thing on its
/// own path was a revocation), key included since that is what a signature is actually checked
/// against.</summary>
public sealed record TrustedNode(string NodeId, string PublicKeyLine, string Fingerprint, DateTimeOffset IssuedAt);

/// <summary>
/// One key in an owner's root authority (idea 6be68ee2: "an owner's root authority is a ranked set
/// of root keys recoverable by succession"). <see cref="IntroducedByNodeId"/> is null for K0 —
/// <c>root.yaml</c>'s own key, the identity that never changes — and otherwise the node id whose own
/// vouched key a validated <c>owners/&lt;root&gt;/rotations/&lt;n&gt;.yaml</c> promoted. Rank is this
/// list's own order: K0 first, then each rotation in the order it actually landed on the ledger, and
/// an earlier key always outranks a later one (only a key ranked strictly above another may revoke
/// it — <see cref="GitLedgerChainReader"/>'s own succession pass is where that rule is enforced).
/// </summary>
public sealed record LiveRootKey(string PublicKeyLine, string Fingerprint, string? IntroducedByNodeId);

/// <summary>
/// One owner root's own chain: its self-certified root key, plus every node currently vouched into
/// it (latest of vouch or revocation, in ref commit order, already resolved — a revoked node is
/// simply absent here). Computed entirely from <c>refs/hall9k/ledger/owners/&lt;root&gt;</c>,
/// independent of whether this root happens to be a project member — that gate is
/// <see cref="TrustChain.IsAllowedSigner"/>'s, not this type's.
/// </summary>
/// <param name="RootNodeId">
/// The node whose own key established this root (<c>h9k project join</c>'s no-<c>--owner</c>
/// path), when <see cref="GitLedgerChainReader"/> could name it from the ledger — null when no
/// node's own self-announced <c>node.yaml</c> currently self-consistently claims this root's key,
/// or when a caller built this record by hand with no opinion on it (every pre-existing test above
/// this field). The root has no <c>owners/&lt;root&gt;/nodes/&lt;id&gt;.yaml</c> vouch entry of its
/// own — a root never vouches itself — so without this, the fleet <see cref="FleetNodeIds"/>
/// computes would silently exclude the one node an owner is guaranteed to actually have.
/// </param>
/// <param name="RevokedNodeIds">
/// Every node id this root's own chain walk found revoked more recently than it was ever vouched —
/// including a node id that was never vouched at all, which is exactly the root's own node: it has
/// no <c>owners/&lt;root&gt;/nodes/&lt;id&gt;.yaml</c> entry for <see cref="Nodes"/> to drop on
/// revocation, so <c>owners/&lt;root&gt;/revoked/&lt;id&gt;.yaml</c> is the only record a revocation
/// of it ever leaves. Consulted by <see cref="FleetNodeIds"/> alone, so <see cref="RootNodeId"/>
/// itself is never cleared here — the root's own node id stays revoked until a later
/// <c>owners/&lt;root&gt;/nodes/&lt;id&gt;.yaml</c> vouch for that same id lands, which
/// <see cref="GitLedgerChainReader"/> both removes from <see cref="RevokedNodeIds"/> and adds to
/// <see cref="Nodes"/> in the identical commit that applies it, so the id returns to the fleet
/// through <see cref="Nodes"/> rather than through <see cref="RootNodeId"/> specifically
/// (independent pre-PR review, cycle 1, conformance lens, medium: a revoked root node stayed in the
/// fleet forever, disagreeing with <c>h9k node revoke</c>'s own success message).
/// </param>
public sealed record TrustedOwner(
    string RootFingerprint, string RootPublicKeyLine, IReadOnlyList<TrustedNode> Nodes, string? RootNodeId = null,
    IReadOnlySet<string>? RevokedNodeIds = null, IReadOnlyList<TrustedNode>? EverEnrolledNodes = null,
    IReadOnlyList<LiveRootKey>? RootKeys = null, IReadOnlyList<string>? SuccessorNodeIds = null)
{
    /// <summary>Never null, whatever a caller passed the primary constructor: a pre-existing
    /// four-argument construction (every call site that predates this field) gets an empty set
    /// rather than a null every reader would otherwise have to guard against.</summary>
    public IReadOnlySet<string> RevokedNodeIds { get; init; } = RevokedNodeIds ?? new HashSet<string>();

    /// <summary>
    /// Every node this root's chain has ever successfully enrolled — by an ordinary vouch or by a
    /// carried bundle's own first establishment — current or since revoked, never shrinking once a
    /// node lands here (task f53fecfd: <see cref="GitLedgerChainReader"/>'s own genesis check
    /// alone reads this, never any later membership write). Never null, on the same terms
    /// <see cref="RevokedNodeIds"/> already is: a pre-existing construction gets an empty list.
    /// </summary>
    public IReadOnlyList<TrustedNode> EverEnrolledNodes { get; init; } = EverEnrolledNodes ?? [];

    /// <summary>
    /// This root's own ranked key set (idea 6be68ee2), K0 (<see cref="RootPublicKeyLine"/>) always
    /// first: an act is root-authorized when any key in this list signs it
    /// (<see cref="OwnerChainAuthorization"/>), never only the single "current" one, so a members
    /// write K0 ever signed keeps verifying after a later rotation adds K1. Never null, on the same
    /// terms <see cref="RevokedNodeIds"/> already is: a pre-existing construction without an opinion
    /// on succession gets exactly K0, the one key every owner has always had.
    /// </summary>
    public IReadOnlyList<LiveRootKey> RootKeys { get; init; } = RootKeys ?? [new LiveRootKey(RootPublicKeyLine, RootFingerprint, null)];

    /// <summary>
    /// Node ids this root has currently listed as a successor (a root-signed
    /// <c>owners/&lt;root&gt;/successors/&lt;node-id&gt;.yaml</c> that has not since rotated in or
    /// been revoked) — a candidate for <see cref="RootKeys"/>, not yet a member of it. Never null, on
    /// the same terms <see cref="RevokedNodeIds"/> already is.
    /// </summary>
    public IReadOnlyList<string> SuccessorNodeIds { get; init; } = SuccessorNodeIds ?? [];

    /// <summary>Whether <paramref name="fingerprint"/> is one of this root's own live keys — K0, or
    /// a key a validated rotation added.</summary>
    public bool IsLiveRootKey(string fingerprint) => RootKeys.Any(key => key.Fingerprint == fingerprint);

    /// <summary>Whether <paramref name="fingerprint"/> is this root's own key or a currently vouched node's.</summary>
    public bool Contains(string fingerprint) =>
        RootFingerprint == fingerprint || Nodes.Any(node => node.Fingerprint == fingerprint);

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is currently vouched specifically for
    /// <paramref name="nodeId"/> — the root's own key qualifies only for <see cref="RootNodeId"/>
    /// itself (never null-safe against an arbitrary node id: idea 6be68ee2, trust-ledger finding 7),
    /// and a vouched node's key only counts here when it is bound to the exact node id it was
    /// vouched under. Reusing one vouched node's key to speak for a different node id is refused
    /// (independent pre-PR review, cycle 1, conformance and adversarial lenses, medium): a member
    /// who overwrites another node's own self-announced <c>node.yaml</c> to carry their own key
    /// must never let that key answer as if it were the original node — and the identical rule now
    /// applies to the root's own key: a node file that merely repeats the root's own public key
    /// answers for it only when <see cref="GitLedgerChainReader"/> has actually attached that exact
    /// node id as <see cref="RootNodeId"/> (that field's own doc: only when the node file's newest
    /// commit is signed by the root key and still claims that root), never for a second node that
    /// happens to declare the same key without ever having been established as the root's own device.
    /// <para>
    /// Enforced at two callers: <see cref="Hall9k.Connectors.Messaging.GitLedgerMessageTransport.ReadSinceAsync"/>
    /// refuses a sender outright when its own node file's key fails this check
    /// (<c>chain.IsAllowedSigner(senderFingerprint, senderNodeId)</c>), and
    /// <see cref="Hall9k.Connectors.Replication.EventReplicationInbox.EvaluateGatedEvent"/> uses this
    /// same rule to decide whether a project-settings-shaped replicated event's own sender currently
    /// belongs to an owner's chain for that sender's node id. A root node refused by either — one
    /// whose own node file is missing, or whose newest commit no longer self-certifies as this root's
    /// device — is repaired only by re-running <c>h9k project join</c> against that root's own key: no
    /// start-up path rewrites a root's node file on its behalf (<c>NodeFileWriter.RefreshGitHubDeclarationAsync</c>
    /// never creates one, only ever updates an existing file's GitHub declaration).
    /// </para>
    /// </summary>
    public bool ContainsForNode(string fingerprint, string nodeId) =>
        (RootFingerprint == fingerprint && RootNodeId == nodeId)
        || Nodes.Any(node => node.Fingerprint == fingerprint && node.NodeId == nodeId);

    /// <summary>
    /// This owner's own fleet, as a de-duplicated set of node ids: <see cref="RootNodeId"/> (the
    /// root's own node, when the ledger names it and <see cref="RevokedNodeIds"/> does not currently
    /// name it revoked) plus every currently vouched node — the one definition every enumerator of
    /// "which nodes may act for this owner" shares (<c>h9k task assign --node</c>'s own placement
    /// resolution, <c>h9k project members</c>, replication candidate ranking, the voucher-tier
    /// lookup), rather than each reading <see cref="Nodes"/> alone and separately forgetting the
    /// root has no vouch entry of its own. De-duplicated because an install that already worked
    /// around the root-fleet gap this method exists to close by running <c>h9k node vouch</c>
    /// against its own root ends up with that same node id in both <see cref="RootNodeId"/> and
    /// <see cref="Nodes"/> (independent pre-PR review, cycle 1, adversarial lens, low) — every
    /// caller gets one id per node either way, rather than each having to de-duplicate for itself.
    /// </summary>
    public IEnumerable<Guid> FleetNodeIds()
    {
        HashSet<Guid> seen = [];

        if (RootNodeId is { } rootNodeId && !RevokedNodeIds.Contains(rootNodeId)
            && Guid.TryParse(rootNodeId, out Guid rootId) && seen.Add(rootId))
        {
            yield return rootId;
        }

        foreach (TrustedNode node in Nodes)
        {
            if (Guid.TryParse(node.NodeId, out Guid nodeId) && seen.Add(nodeId))
            {
                yield return nodeId;
            }
        }
    }

    /// <summary>
    /// The node id holding this root's own highest-ranked live root key (idea 6be68ee2, companion
    /// 1bb803e1: "addressed to the node holding the highest-ranked live root key") - the node an
    /// owner-act request routes a root-only write to when the asking node's own key is not itself
    /// one. Walks <see cref="RootKeys"/> in rank order (K0 first): K0's own holder is
    /// <see cref="RootNodeId"/> (<see cref="LiveRootKey.IntroducedByNodeId"/> is always null for K0),
    /// which can be null on an older ledger that never resolved one, and every rotation's own holder
    /// is named directly by its own <see cref="LiveRootKey.IntroducedByNodeId"/> - never null once a
    /// rotation has actually landed. Returns the first one that resolves to an actual node id, so a
    /// K0 whose own node the ledger cannot name still falls through to a validated rotation's node
    /// rather than reporting nothing when a perfectly good target exists. Null only when nothing in
    /// this root's own live-key chain can be named at all - the caller's own cue that nobody could
    /// ever receive the write.
    /// </summary>
    public Guid? ResolveRootActingNodeId()
    {
        foreach (LiveRootKey rootKey in RootKeys)
        {
            string? nodeId = rootKey.IntroducedByNodeId ?? RootNodeId;
            if (nodeId is not null && Guid.TryParse(nodeId, out Guid parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}

/// <summary>One project member, chain-validated: the write that established or last changed this
/// entry was itself signed by a key already trusted at that point in the members ref's own
/// history (or, for the very first entry, self-written — idea 202383dc's genesis rule).</summary>
public sealed record ProjectMember(string RootFingerprint, MembershipRole Role, DateTimeOffset IssuedAt);

/// <summary>
/// The GitHub account one node declares for itself in its own <c>node.yaml</c>, taken only from a
/// file whose newest commit is signed by that file's own public key (<see cref="KeyFingerprint"/>).
/// A claim, not proof: nothing here says the person behind the key controls the account.
/// <see cref="DeclaredAt"/> is the committing time, used only to pick the newest login when one
/// account id is declared under two names.
/// </summary>
public sealed record NodeGitHubDeclaration(string NodeId, string KeyFingerprint, DeclaredGitHubAccount Account, DateTimeOffset DeclaredAt);

/// <summary>
/// The display name one node declares for its owner in its own <c>node.yaml</c> (task e6744304),
/// taken only from a file whose newest commit is signed by that file's own public key
/// (<see cref="KeyFingerprint"/>), the identical rule <see cref="NodeGitHubDeclaration"/> already
/// carries. A label only: nothing here ever feeds a trust or cross-check decision.
/// <see cref="DeclaredAt"/> is the time this exact name was last set — the oldest commit,
/// contiguous from the file's own newest, that already carried it (<c>GitLedgerChainReader</c>'s own
/// <c>FieldSettledAtAsync</c>) — used only to pick the newest name when a member's own nodes
/// disagree. Deliberately not simply the file's own newest commit time: the GitHub declaration
/// shares this same file and is refreshed on its own schedule, so a GitHub-only rewrite must never
/// make an unrelated, unchanged name look newer than a different name set more recently elsewhere
/// (independent pre-PR review, cycle 1, adversarial lens, medium).
/// </summary>
public sealed record NodeDisplayNameDeclaration(string NodeId, string KeyFingerprint, DisplayName Name, DateTimeOffset DeclaredAt);

/// <summary>
/// A vouch, revocation, or membership write the chain read found but could not verify — its signer
/// traced back to no currently trusted key, so the file or event it names was never applied to
/// <see cref="TrustChain.OwnerChains"/> or <see cref="TrustChain.Members"/>. Named here rather than
/// silently dropped (independent pre-PR review, cycle 1, conformance lens, medium: "an unverifiable
/// writer's files and envelopes are ignored and the writer is named").
/// </summary>
public sealed record UnverifiedLedgerWrite(string Kind, string Identifier, string RootFingerprint, string Reason);

/// <summary>
/// The result of walking a project's ledger — every owner root's own chain, and current project
/// membership derived from replaying <c>refs/hall9k/ledger/members</c> against those chains (idea
/// 202383dc, T1). Recomputed fresh on every read (<see cref="ILedgerChainReader.ComputeAsync"/>),
/// never cached across calls: a revocation or a removal takes effect the moment the next read walks
/// the ledger again.
/// <para>
/// <see cref="GenesisRootFingerprint"/> is the fingerprint named by the very first commit
/// <c>refs/hall9k/ledger/members</c>' own history ever holds (idea 202383dc, M2) — set the moment
/// genesis is decided, whether or not that commit actually self-certifies, since it names a fact
/// about the ref's own immutable history, not a verdict on trust. Every node that fetches the
/// identical shared repository replays the identical commit history and lands on the identical
/// value, regardless of any install's own locally-minted project id. Null only when the members
/// ref itself has no commit yet (the project's ledger was never initialized — <c>h9k project
/// join</c> never ran there). This is an owner fact alone from here on — it identifies who wrote
/// genesis, never the project itself (idea 202383dc, M2 follow-up, ruled by Brian 2026-09-17 after
/// the c8dd149c replication dispute exposed that two projects sharing one genesis owner shared
/// this fingerprint too); <see cref="ProjectKey"/> is the project's own wire identity now.
/// </para>
/// <para>
/// <see cref="ProjectKey"/> is the 26-character ULID <c>h9k project join</c> mints fresh (Cysharp's
/// Ulid) the moment it writes this project's genesis members commit, recorded as that commit's own
/// <c>project_key</c> field and read back here from the genesis fingerprint's own current
/// <c>members/&lt;fingerprint&gt;.yaml</c> content — so a later, authorized rewrite of that same
/// file (<c>h9k project assign-key</c>, the one-time backfill for a ledger whose genesis predates
/// this piece) is picked up the identical way, without this reader caring whether the key arrived
/// with genesis or after it. Generated once, per project, never derived from anything about the
/// owner who happened to write genesis: two projects sharing one genesis owner's root fingerprint
/// (idea 202383dc's own origin incident — hall9k and odonomics on Brian's machine) get two
/// unrelated keys. Null until a genesis commit exists and actually carries one — a ledger written
/// before this piece shipped, or mid-adoption before <c>h9k project assign-key</c> has run.
/// </para>
/// </summary>
public sealed record TrustChain(
    IReadOnlyDictionary<string, TrustedOwner> OwnerChains,
    IReadOnlyList<ProjectMember> Members,
    IReadOnlyList<UnverifiedLedgerWrite>? UnverifiedWrites = null,
    string? GenesisRootFingerprint = null,
    string? ProjectKey = null,
    IReadOnlyDictionary<string, NodeGitHubDeclaration>? NodeDeclarations = null,
    IReadOnlyDictionary<string, NodeDisplayNameDeclaration>? NodeDisplayNames = null)
{
    /// <summary>
    /// Every node's verified GitHub declaration, keyed by node id: the file's newest commit was signed
    /// by its own public key. Never null, on the same terms as <see cref="UnverifiedWrites"/>; a node
    /// file written before the declaration existed simply has no entry.
    /// </summary>
    public IReadOnlyDictionary<string, NodeGitHubDeclaration> NodeDeclarations { get; init; } =
        NodeDeclarations ?? new Dictionary<string, NodeGitHubDeclaration>();

    /// <summary>
    /// Every node's verified display-name declaration, keyed by node id, on the identical terms
    /// <see cref="NodeDeclarations"/> already carries (task e6744304). Never null; a node file
    /// written before the field existed, or whose owner never set a name, simply has no entry.
    /// </summary>
    public IReadOnlyDictionary<string, NodeDisplayNameDeclaration> NodeDisplayNames { get; init; } =
        NodeDisplayNames ?? new Dictionary<string, NodeDisplayNameDeclaration>();

    /// <summary>
    /// The distinct GitHub accounts declared across <paramref name="root"/>'s own nodes: distinct by
    /// account id with the newest login per id (a renamed account is one entry), one entry per
    /// account when two nodes declare two accounts. A commit time has one-second resolution, so two
    /// declarations of one id in the same second tie; the higher node id (a later-minted node) then
    /// wins, so the answer never depends on the order the fleet lists its nodes in. A declaration counts only for a node in this
    /// root's fleet whose file carries the very key the chain vouched for that node id, so a file
    /// someone else rewrote under their own key adds nothing here.
    /// </summary>
    public IReadOnlyList<DeclaredGitHubAccount> DeclaredAccountsOf(string root) =>
        OwnerChains.TryGetValue(root, out TrustedOwner? owner)
            ? [.. owner.FleetNodeIds()
                .Select(nodeId => NodeDeclarations.GetValueOrDefault(nodeId.ToString()))
                .OfType<NodeGitHubDeclaration>()
                .Where(declaration => owner.ContainsForNode(declaration.KeyFingerprint, declaration.NodeId))
                .GroupBy(declaration => declaration.Account.AccountId)
                .Select(group => group
                    .OrderByDescending(declaration => declaration.DeclaredAt)
                    .ThenByDescending(declaration => declaration.NodeId, StringComparer.Ordinal)
                    .First().Account)]
            : [];

    /// <summary>
    /// The single GitHub account <paramref name="root"/>'s own fleet most recently declared (task
    /// b7d8222e): unlike <see cref="DeclaredAccountsOf"/>, which keeps one entry per distinct
    /// account, this picks the one newest declaration across every account a member's fleet has
    /// ever declared, since a member's own label carries at most one login. Ordered by
    /// <see cref="NodeGitHubDeclaration.DeclaredAt"/>, ties broken by the higher node id — the
    /// identical precedence <see cref="DeclaredAccountsOf"/> already applies within one account.
    /// Null when nobody in this root's own fleet has declared one.
    /// </summary>
    public DeclaredGitHubAccount? NewestDeclaredAccountOf(string root) =>
        OwnerChains.TryGetValue(root, out TrustedOwner? owner)
            ? owner.FleetNodeIds()
                .Select(nodeId => NodeDeclarations.GetValueOrDefault(nodeId.ToString()))
                .OfType<NodeGitHubDeclaration>()
                .Where(declaration => owner.ContainsForNode(declaration.KeyFingerprint, declaration.NodeId))
                .OrderByDescending(declaration => declaration.DeclaredAt)
                .ThenByDescending(declaration => declaration.NodeId, StringComparer.Ordinal)
                .Select(declaration => declaration.Account)
                .FirstOrDefault()
            : null;

    /// <summary>
    /// The newest display name declared across <paramref name="root"/>'s own nodes (task e6744304),
    /// or <see cref="DisplayName.None"/> when none of them declares one. A label only: this is never
    /// consulted by <see cref="IsAllowedSigner(string)"/> or any other trust or cross-check decision
    /// here, unlike <see cref="DeclaredAccountsOf"/>'s own accounts. Ordered by
    /// <see cref="NodeDisplayNameDeclaration.DeclaredAt"/> (the time each name was actually last set,
    /// not merely the newest commit touching that node's file), then by the higher node id when two
    /// nodes' own values tie in the same second, so the answer never depends on fleet list order.
    /// A declaration counts only for a node in this root's fleet whose file carries the very key the
    /// chain vouched for that node id, the same <see cref="TrustedOwner.ContainsForNode"/> gate
    /// <see cref="DeclaredAccountsOf"/> applies.
    /// </summary>
    public DisplayName DisplayNameOf(string root) =>
        OwnerChains.TryGetValue(root, out TrustedOwner? owner)
            ? owner.FleetNodeIds()
                .Select(nodeId => NodeDisplayNames.GetValueOrDefault(nodeId.ToString()))
                .OfType<NodeDisplayNameDeclaration>()
                .Where(declaration => owner.ContainsForNode(declaration.KeyFingerprint, declaration.NodeId))
                .OrderByDescending(declaration => declaration.DeclaredAt)
                .ThenByDescending(declaration => declaration.NodeId, StringComparer.Ordinal)
                .Select(declaration => declaration.Name)
                .FirstOrDefault() ?? DisplayName.None
            : DisplayName.None;

    /// <summary>Never null, whatever a caller passed the primary constructor: a two-argument
    /// construction (every call site that predates this field) gets an empty list rather than a
    /// null every reader would otherwise have to guard against.</summary>
    public IReadOnlyList<UnverifiedLedgerWrite> UnverifiedWrites { get; init; } = UnverifiedWrites ?? [];

    public static readonly TrustChain Empty = new(new Dictionary<string, TrustedOwner>(), []);

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is currently allowed to write or sign a ledger ref
    /// for this project: it belongs to some owner's chain, and that owner is itself a current
    /// project member — a stranger's own self-certified root and vouched nodes are always excluded
    /// here, however internally consistent their own owner ref is, because they were never added
    /// to <see cref="Members"/>.
    /// </summary>
    public bool IsAllowedSigner(string fingerprint) =>
        Members.Any(member =>
            OwnerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner) && owner.Contains(fingerprint));

    /// <summary>
    /// The messages-ref overload: <paramref name="fingerprint"/> must not only trace back to a
    /// current project member's own chain, it must be the exact key that chain vouched for
    /// <paramref name="nodeId"/> specifically (<see cref="TrustedOwner.ContainsForNode"/>) — never
    /// merely some other node's key the same owner happens to have vouched. Without this, a member
    /// could overwrite <paramref name="nodeId"/>'s own self-announced node file with their own key
    /// and have their messages accepted as if they were that node (independent pre-PR review,
    /// cycle 1, conformance and adversarial lenses, medium).
    /// </summary>
    public bool IsAllowedSigner(string fingerprint, Guid nodeId) =>
        Members.Any(member =>
            OwnerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner)
            && owner.ContainsForNode(fingerprint, nodeId.ToString()));

    /// <summary>
    /// Whether <paramref name="claimedOwnerFingerprint"/>'s own chain vouches
    /// <paramref name="fingerprint"/> for <paramref name="nodeId"/> specifically — the shared check
    /// behind verifying a self-declared owner claim (a claim request's own body,
    /// <c>Hall9k.Domain.Features.Message.MessageEnvelopeV1.FromOwner</c>) against the one fact the
    /// ledger itself can prove, rather than trusting the claim on its own say-so.
    /// <see cref="TrustedOwner.ContainsForNode"/> is the same primitive <see cref="IsAllowedSigner(string, Guid)"/>
    /// itself uses, scoped here to the one owner root the caller is checking rather than "any member
    /// owner" — <c>ClaimRequestWatchLoop.IsRequesterOwnerVerified</c> and
    /// <c>MessageInbox.ReadFromAsync</c> both call this rather than each re-typing the lookup.
    /// <paramref name="fingerprint"/> null never verifies — the honest reading of "nothing to check"
    /// for a claim whose whole purpose is refusing anything this ledger cannot positively vouch for.
    /// </summary>
    public bool VouchesOwnerForNode(string claimedOwnerFingerprint, string? fingerprint, Guid nodeId) =>
        fingerprint is not null
        && OwnerChains.TryGetValue(claimedOwnerFingerprint, out TrustedOwner? owner)
        && owner.ContainsForNode(fingerprint, nodeId.ToString());

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is already enrolled in <paramref name="root"/>'s own
    /// chain — regardless of project membership, the rule <c>h9k node vouch</c>/<c>revoke</c> is
    /// refused against ("any enrolled node of that owner", idea 202383dc): vouching a node into an
    /// owner is that owner's own fact, prior to and independent of whether the owner has been made
    /// a member of any particular project.
    /// </summary>
    public bool IsEnrolledInOwner(string fingerprint, string root) =>
        OwnerChains.TryGetValue(root, out TrustedOwner? owner) && owner.Contains(fingerprint);

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is currently one of <paramref name="root"/>'s own LIVE
    /// ROOT KEYS — K0 or a validated rotation (idea 6be68ee2's ranked root-key set) — never merely a
    /// node vouched into that root's own fleet. The gate a node revocation, a members-ref write, or a
    /// role change requires (idea 6be68ee2, trust-ledger finding 2): a compromised fleet node must
    /// never revoke its own peers or rewrite membership on its own say-so, so
    /// <c>NodeRevokeCommand</c>, <c>ProjectInviteCommand</c>, <c>ProjectMemberRemoveCommand</c>,
    /// <c>ProjectAssignKeyCommand</c>, and <c>ProjectMemberReaffirmCommand</c> all refuse before any
    /// push on this, never on <see cref="IsEnrolledInOwner(string,string)"/> (which
    /// <c>NodeVouchCommand</c> and <c>NodeInviteCommand</c> still correctly use — a vouch stays
    /// unaffected) and never by comparing against this node's own identity fingerprint (a promoted
    /// successor's key can never equal it) or <see cref="TrustedOwner.RootNodeId"/> (null on an older
    /// ledger that never resolved a root node id).
    /// </summary>
    public bool IsLiveRootKeyOfOwner(string fingerprint, string root) =>
        OwnerChains.TryGetValue(root, out TrustedOwner? owner) && owner.IsLiveRootKey(fingerprint);

    public MembershipRole? RoleOf(string root) =>
        Members.FirstOrDefault(member => member.RootFingerprint == root) is { } found ? found.Role : null;
}

/// <summary>
/// Computes <see cref="TrustChain"/> for a project's ledger — the one place chain verification
/// happens (idea 202383dc, T1): allowed signers are roots plus nodes vouched into them minus
/// revocations, in ref order, restricted to owners the members ref itself currently recognizes.
/// <see cref="GitLedgerChainReader"/> is the real implementation, over real git plumbing (the
/// second place, besides A1's own <c>GitLedgerTests</c>, a throwaway bare repository is used in
/// tests — Brian's 2026-09-13 testing rule).
/// </summary>
public interface ILedgerChainReader
{
    Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken);
}
