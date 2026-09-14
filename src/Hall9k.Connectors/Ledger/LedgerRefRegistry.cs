using System.Collections.Concurrent;

namespace Hall9k.Connectors.Ledger;

/// <summary>
/// How one registered entry names the refs it covers. Most ledger refs are known up front
/// (<c>refs/hall9k/ledger/records</c>) and register <see cref="Exact"/>; a ref whose own name is
/// not known until something that names it exists — a node's outbox, named by that node's own id
/// — registers the fixed <see cref="Prefix"/> everything of that shape lives under instead.
/// </summary>
public enum LedgerRefKind
{
    Exact,
    Prefix,
}

/// <summary>
/// One entry in <see cref="LedgerRefRegistry"/>: what <see cref="LedgerRefRegistry.FetchRefspecs"/>
/// turns into a fetch (and push destination) refspec, and what
/// <see cref="LedgerRefRegistry.IsRegistered"/> matches a caller's ref name against.
/// </summary>
public sealed record LedgerRefEntry(string RefspecSource, LedgerRefKind Kind)
{
    public bool Matches(string refName) => Kind switch
    {
        LedgerRefKind.Exact => refName == RefspecSource,
        LedgerRefKind.Prefix => refName.StartsWith(RefspecSource, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// The refspec fragment this entry contributes. A ledger ref mirrors the remote ref onto the
    /// identical local name (<c>+refs/hall9k/ledger/records:refs/hall9k/ledger/records</c>) rather
    /// than into a <c>refs/remotes/origin/*</c> remote-tracking name the way an ordinary branch
    /// fetch does — a ledger ref is never a branch and nothing ever merges it into one.
    /// </summary>
    public string Refspec => Kind switch
    {
        LedgerRefKind.Exact => $"+{RefspecSource}:{RefspecSource}",
        LedgerRefKind.Prefix => $"+{RefspecSource}*:{RefspecSource}*",
        _ => throw new InvalidOperationException($"Unknown {nameof(LedgerRefKind)} {Kind}."),
    };
}

/// <summary>
/// The one place every <c>refs/hall9k/</c> ref any caller uses is named. <see cref="GitLedger"/>
/// refuses to touch a ref that is not registered here first — that gate is <see
/// cref="IsRegistered"/>, not <see cref="FetchRefspecs"/> — and an ordinary <c>git fetch
/// origin</c> — the project's own <c>+refs/heads/*:refs/remotes/origin/*</c> — never brings one
/// down, because nothing in <see cref="GitLedger"/> ever relies on that configured refspec: every
/// operation builds its own explicit single-ref refspec from the exact ref name it was called
/// with (<c>+refName:refName</c>) on the command line instead. That is deliberate even for a
/// <see cref="LedgerRefKind.Prefix"/> entry such as <see cref="MessagesPrefix"/>: the wildcard
/// <see cref="LedgerRefEntry.Refspec"/> below covers every ref under that prefix, but a single
/// call only ever touches the one concrete ref name it was handed, never every node's outbox at
/// once — so <see cref="GitLedger"/> never reads <see cref="FetchRefspecs"/> or
/// <see cref="LedgerRefEntry.Refspec"/> itself; both exist for a caller that genuinely wants the
/// whole-prefix shape (and for the tests below that assert it), not for <see cref="GitLedger"/>'s
/// own per-call fetch or push.
/// <para>
/// Registration is idempotent by value (<see cref="LedgerRefEntry"/> is a record, so two calls
/// naming the identical ref/prefix collapse to one entry) — this is process-wide static state
/// shared by every caller and, in tests, by every test in the assembly, so a second registration
/// of the same ref from a second caller (or a second test) is a no-op rather than a duplicate.
/// </para>
/// </summary>
public static class LedgerRefRegistry
{
    private static readonly ConcurrentDictionary<LedgerRefEntry, byte> Entries = new();

    /// <summary>
    /// The records ref (A3, not yet built): one file per task record, holder included. Registered
    /// here from A1's own first commit, even though nothing yet reads or writes its content — the
    /// ref name belongs to the ledger's own namespace, not to whichever later task first calls it.
    /// </summary>
    public static readonly LedgerRefEntry Records = RegisterExact("refs/hall9k/ledger/records");

    /// <summary>
    /// Every node's own outbox (messages, not yet built): <c>refs/hall9k/messages/&lt;node-id&gt;</c>,
    /// one writer per node. Registered as a prefix because no node id exists yet for a literal
    /// name the way <see cref="Records"/> has one.
    /// </summary>
    public static readonly LedgerRefEntry MessagesPrefix = RegisterPrefix("refs/hall9k/messages/");

    /// <summary>
    /// Every owner's root (A2a): <c>refs/hall9k/ledger/owners/&lt;fingerprint&gt;</c>, one ref per
    /// fingerprint, holding that root's <c>root.yaml</c> and, once a self-created root turns out to
    /// belong to another root, its own <c>retired.yaml</c>. Registered as a prefix because no
    /// fingerprint exists for a literal name until a node's key does.
    /// </summary>
    public static readonly LedgerRefEntry OwnersPrefix = RegisterPrefix("refs/hall9k/ledger/owners/");

    /// <summary>
    /// Every node's own identity file (A2a): <c>refs/hall9k/ledger/nodes/&lt;node-id&gt;</c>, one
    /// writer per node, the same reason <see cref="MessagesPrefix"/> is a prefix rather than an
    /// exact name.
    /// </summary>
    public static readonly LedgerRefEntry NodesPrefix = RegisterPrefix("refs/hall9k/ledger/nodes/");

    public static LedgerRefEntry RegisterExact(string refName) => Register(refName, LedgerRefKind.Exact);

    public static LedgerRefEntry RegisterPrefix(string prefix) => Register(
        prefix.EndsWith('/')
            ? prefix
            : throw new ArgumentException($"{prefix} must end in '/' to register as a prefix.", nameof(prefix)),
        LedgerRefKind.Prefix);

    /// <summary>
    /// The one namespace every registration is confined to, enforced here rather than trusted to
    /// each call site — a caller that mistypes <c>refs/heads/main</c> into <see cref="RegisterExact"/>
    /// would otherwise make <see cref="GitLedger"/> fetch and push an ordinary branch, silently
    /// violating the <see cref="ILedger"/> contract that every ledger ref lives under this prefix.
    /// </summary>
    private const string Namespace = "refs/hall9k/";

    private static LedgerRefEntry Register(string refspecSource, LedgerRefKind kind)
    {
        if (!refspecSource.StartsWith(Namespace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{refspecSource} does not start with {Namespace} — every ledger ref or prefix "
                + $"registered here must live under that namespace.",
                nameof(refspecSource));
        }

        LedgerRefEntry entry = new(refspecSource, kind);
        Entries.TryAdd(entry, 0);
        return entry;
    }

    /// <summary>
    /// The whole-prefix refspec each registered entry would use if something ever fetched or
    /// pushed an entire prefix at once — one per registered entry. <see cref="GitLedger"/> itself
    /// never reads this: its own per-call refspec is always the exact single ref name it was
    /// invoked with, built independently of anything registered here.
    /// </summary>
    public static IReadOnlyCollection<string> FetchRefspecs => Entries.Keys.Select(entry => entry.Refspec).ToList();

    /// <summary>Whether a ref name is covered by some registered exact name or prefix.</summary>
    public static bool IsRegistered(string refName) => Entries.Keys.Any(entry => entry.Matches(refName));
}
