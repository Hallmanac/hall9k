using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Connectors.Messaging;

/// <summary>
/// The one shared project-key mismatch check (idea 6be68ee2, trust-ledger finding 13), replacing
/// the three private, near-identical copies <see cref="Hall9k.Connectors.Messaging.MessageInbox"/>,
/// <see cref="Hall9k.Connectors.Replication.EventCatchUpInbox"/>, and
/// <see cref="Hall9k.Connectors.Replication.EventReplicationInbox"/> each carried. <see cref="IsMismatch"/>
/// is the pure comparison every caller now applies unconditionally — never gated behind
/// <c>candidateKey is { Length: 26 }</c> the way the three copies used to be, which is exactly what
/// let a null or malformed key slip through as "no opinion" even once this project already knew its
/// own key: once <paramref name="localProjectKey"/> is known, a null or non-26-character candidate is
/// refused the identical way a genuine mismatch is. A project with no key of its own yet still has no
/// opinion, whatever the candidate looks like — the local fact idea 202383dc, M2's own ruling turns
/// on, and the reason existing fleets on a build old enough to still carry an unkeyed project never
/// stall.
/// <para>
/// <see cref="IsMismatchAsync"/> is the thin async wrapper every reader actually calls: it applies
/// the pure check first, then — only when this project has no key of its own yet and the candidate is
/// otherwise well-formed — falls back to the one lookup <see cref="IsMismatch"/> cannot do on its own,
/// a genuinely 26-character candidate resolving to some OTHER local project's own recorded key. That
/// fallback is unrelated to the null/malformed-key fix (idea 202383dc, M2's own original design) and
/// is preserved exactly as it was.
/// </para>
/// </summary>
public static class ProjectKeyMismatch
{
    private const int KeyLength = 26;

    /// <summary>
    /// Pure: true when this project already knows its own key
    /// (<paramref name="localProjectKey"/> is not null) and <paramref name="candidateKey"/> does not
    /// match it — including a null candidate or one that is not exactly <see cref="KeyLength"/>
    /// characters. Always false when this project has no key of its own yet, whatever the candidate
    /// looks like: there is nothing here to judge a mismatch against.
    /// </summary>
    public static bool IsMismatch(string? candidateKey, string? localProjectKey) =>
        localProjectKey is not null
        && (candidateKey is not { Length: KeyLength } || candidateKey != localProjectKey);

    /// <summary>
    /// The async wrapper every reader calls: <see cref="IsMismatch"/> first, then — only once this
    /// project has no key of its own yet (<paramref name="localProjectKey"/> null) and
    /// <paramref name="candidateKey"/> is genuinely <see cref="KeyLength"/> characters — a lookup for
    /// whether some OTHER local project already carries this exact key, so a project too new or too
    /// far behind to have read its own key back yet still refuses an envelope this node can already
    /// prove belongs elsewhere.
    /// </summary>
    public static async Task<bool> IsMismatchAsync(
        IDocumentSession session, Guid projectId, string? candidateKey, string? localProjectKey,
        CancellationToken cancellationToken)
    {
        if (IsMismatch(candidateKey, localProjectKey))
        {
            return true;
        }

        if (localProjectKey is not null || candidateKey is not { Length: KeyLength })
        {
            return false;
        }

        ProjectDetails? resolvedByKey = await session.Query<ProjectDetails>()
            .Where(candidate => candidate.ProjectKey == candidateKey)
            .FirstOrDefaultAsync(cancellationToken);
        return resolvedByKey is not null && resolvedByKey.Id != projectId;
    }
}
