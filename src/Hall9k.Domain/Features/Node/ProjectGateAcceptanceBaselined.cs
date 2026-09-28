namespace Hall9k.Domain.Features.Node;

/// <summary>
/// This node's own one-time baseline sweep for gate-set acceptance has run (security review idea
/// 6be68ee2, process-injection finding 1, the local half). At the daemon's first start after this
/// feature shipped, every project that already existed on this node with no
/// <c>ProjectGateSetAccepted</c> of its own is baselined once to its then-current gate set — the
/// accepted weakness named in that origin finding: a malicious set that arrived before that first
/// start is baselined as accepted along with everything else, since nothing on this node yet
/// distinguishes it from a set an operator actually vetted. A project that joins <em>after</em> that
/// first start is never baselined, so its first replicated set holds until <c>h9k project
/// accept-gates</c>, same as any later change.
/// <para>
/// Appended exactly once per node, the identical <see cref="ReplicationSwitchedOn"/> idiom: a
/// second daemon start must never re-run the baseline, because by then a project that joined in the
/// meantime also reads "nothing accepted yet" — the same shape the baseline exists to seed, not to
/// keep seeding forever. Node-scoped, since it is a fact about this install alone.
/// </para>
/// </summary>
public sealed record ProjectGateAcceptanceBaselined(Guid Id, DateTimeOffset BaselinedAt);
