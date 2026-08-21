using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// The same connection, registered again with different details — a rotated token, a moved
/// site, a different account on the same tenant.
/// <para>
/// It exists so that re-running <c>h9k connection add</c> is not a dead end. A refusal there
/// would be the one error shape an agent cannot self-correct from (AGENTS.md, CLI standards)
/// because there is no remove command to send anybody to, and a second connection would leave
/// two Jira accounts with nothing saying which one a project uses. Appending to the existing
/// stream keeps both the identity (projects bind to this id and keep working) and the history
/// (what the connection used to be is still on the stream).
/// </para>
/// </summary>
public sealed record ConnectionReregistered(
    Guid Id,
    WorkItemProvider Provider,
    string ExternalAccountId,
    CredentialReference CredentialReference,
    DateTimeOffset ReregisteredAt,
    Uri? SiteUrl = null);
