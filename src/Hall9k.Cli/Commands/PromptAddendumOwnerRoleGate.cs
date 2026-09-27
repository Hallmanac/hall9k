using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Projections;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>Why <see cref="PromptAddendumOwnerRoleGate.ResolveAsync"/> answered other than
/// <see cref="OwnerRole"/> — distinct enough that the caller can print a message an agent can
/// actually self-correct from (AGENTS.md's own CLI standard) rather than one flat "not an
/// Owner-role member" line that reads identically whether this node never joined the project at
/// all or joined it as an ordinary Member (independent pre-PR review, cycle 3, conformance lens,
/// low).</summary>
internal enum OwnerRoleGateVerdict
{
    /// <summary>This node's own owner is currently an Owner-role member — the ordinary, no-warning case.</summary>
    OwnerRole,

    /// <summary>This node has never claimed an owner root for itself at all (<c>h9k project join</c>
    /// never ran), so there is nothing to check the chain against.</summary>
    NeverJoined,

    /// <summary>This node's own owner has a claimed root, but the project's own live chain does not
    /// currently recognize it as Owner-role.</summary>
    NotOwnerRole,
}

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
    /// Why this node's own owner is or is not currently an Owner-role member of
    /// <paramref name="project"/>. Best-effort on a ledger read failure (network, an unreachable
    /// remote): answers <see cref="OwnerRoleGateVerdict.OwnerRole"/> rather than block the command
    /// or print a warning that might not even be accurate — the daemon's own
    /// <c>PromptAddendaSweepEngine.PushAsync</c> is the actual gate, this is only ever an early,
    /// advisory heads-up.
    /// </summary>
    public static async Task<OwnerRoleGateVerdict> ResolveAsync(
        IQuerySession session, ProjectDetails project, Guid ownerId, ILedgerChainReader chainReader,
        CancellationToken cancellationToken)
    {
        string? ownerRootFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, ownerId, cancellationToken);
        if (ownerRootFingerprint is null)
        {
            return OwnerRoleGateVerdict.NeverJoined;
        }

        try
        {
            TrustChain chain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
            return chain.RoleOf(ownerRootFingerprint) == MembershipRole.Owner
                ? OwnerRoleGateVerdict.OwnerRole
                : OwnerRoleGateVerdict.NotOwnerRole;
        }
        catch (InvalidOperationException)
        {
            return OwnerRoleGateVerdict.OwnerRole;
        }
    }
}
