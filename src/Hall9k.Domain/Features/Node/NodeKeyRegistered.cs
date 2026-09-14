namespace Hall9k.Domain.Features.Node;

/// <summary>
/// This node's own ed25519 signing key, generated once by <c>h9k project join</c> (Hall9k.
/// Connectors.Identity's <c>NodeKeyStore</c>) under the install's own
/// <c>~/.hall9k/keys/&lt;node-id&gt;</c>, never re-generated once recorded. <see cref="PublicKey"/>
/// is the public half alone — the private half never appears on any event, ledger file, or
/// project home (idea 202383dc, A2a).
/// </summary>
public sealed record NodeKeyRegistered(Guid Id, string PublicKey, string KeyFingerprint, DateTimeOffset RegisteredAt);
