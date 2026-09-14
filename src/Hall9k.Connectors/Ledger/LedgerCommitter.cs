namespace Hall9k.Connectors.Ledger;

/// <summary>
/// Who a ledger commit is attributed to. <c>git commit-tree</c> refuses to build a commit at all
/// without an identity, signing or not, and nothing upstream of A2a (node/owner identity, not yet
/// built) exists yet to supply one on a caller's behalf — so every write names its own rather than
/// this component guessing at one (AGENTS.md: never guess at unobserved facts).
/// </summary>
public sealed record LedgerCommitter(string Name, string Email);
