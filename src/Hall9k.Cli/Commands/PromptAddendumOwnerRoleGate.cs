using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Shared by <see cref="ProjectPromptAddendumSetCommand"/> and
/// <see cref="ProjectPromptAddendumRemoveCommand"/> (idea 6be68ee2, trust-ledger finding 6): the
/// ledger holds exactly one file per builder for the whole project
/// (<c>LedgerRefRegistry.PromptAddenda</c>, no owner segment), so
/// <c>PromptAddendaSweepEngine.PushAsync</c> only ever pushes from a node whose own owner is
/// currently an Owner-role member — a member's own push would be refused everywhere it is read
/// back, its own node included. Told here at set/remove time, rather than left to a silent no-op
/// the daemon's next sweep never explains.
/// </summary>
internal static class PromptAddendumOwnerRoleGate
{
    /// <summary>
    /// Whether this node's own owner is currently an Owner-role member of <paramref name="project"/>.
    /// Best-effort on a ledger read failure (network, an unreachable remote): returns true rather
    /// than block the command or print a warning that might not even be accurate — the daemon's own
    /// <c>PromptAddendaSweepEngine.PushAsync</c> is the actual gate, this is only ever an early,
    /// advisory heads-up.
    /// </summary>
    public static async Task<bool> NodeOwnerIsProjectOwnerAsync(
        IQuerySession session, ProjectDetails project, Guid ownerId, ILedgerChainReader chainReader,
        CancellationToken cancellationToken)
    {
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, ownerId, cancellationToken);
        if (ownerRootFingerprint is null)
        {
            return false;
        }

        try
        {
            TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
            return chain.RoleOf(ownerRootFingerprint) == MembershipRole.Owner;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
