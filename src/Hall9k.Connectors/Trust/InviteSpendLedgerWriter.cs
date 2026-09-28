using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// Writes <c>owners/&lt;rootFingerprint&gt;/invites/&lt;inviteId&gt;.yaml</c> with
/// <c>InviteLedgerRecord.Spent</c> true - never restricted to a live root key (idea 1bb803e1
/// leaves this write open, unlike the member-role write it settles): whichever node minted the
/// invite marks its own record spent, whether that node vouched the candidate directly or only
/// after the root's own <c>owner-act</c> outcome came back <c>done</c>
/// (<c>Hall9k.Daemon.Invites.InviteSweepEngine</c>'s own direct path and the requester-side
/// reaction to that outcome both call this). Named in plain <c>&lt;c&gt;</c> text rather than
/// <c>&lt;see cref&gt;</c> (independent pre-PR review, cycle 1, conformance lens, low): this
/// project, Hall9k.Connectors, does not reference Hall9k.Daemon, so a cref naming a Daemon type
/// here can never resolve.
/// </summary>
public static class InviteSpendLedgerWriter
{
    private const int MaxConflictRetries = 5;

    public static async Task WriteAsync(
        ILedger ledger, string repositoryPath, string rootFingerprint, Guid inviteId, string secretHash,
        InviteClaimKind claim, ProjectMemberRole? role, DateTimeOffset expiresAt, LedgerCommitter committer,
        LedgerSigningKey signingKey, CancellationToken cancellationToken)
    {
        InviteLedgerRecord spentRecord = new(secretHash, claim, role, expiresAt, Spent: true);
        string refName = InviteLedgerRecord.RefName(rootFingerprint);
        string path = InviteLedgerRecord.PathFor(rootFingerprint, inviteId);
        string content = spentRecord.ToYaml();

        for (int attempt = 1; attempt <= MaxConflictRetries; attempt++)
        {
            LedgerFile current = await ledger.ReadAsync(repositoryPath, refName, path, cancellationToken);
            if (current.Content == content)
            {
                return;
            }

            LedgerWriteOutcome outcome = await ledger.WriteAsync(
                new LedgerWriteRequest(
                    repositoryPath, refName, path, content, current.BlobId, $"Mark invite {inviteId} spent", committer,
                    signingKey),
                cancellationToken);
            if (outcome.Verdict == LedgerWriteVerdict.Written)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"{path} kept changing out from under this write after {MaxConflictRetries} attempts - "
            + "something else is writing it at the same time; will retry next sweep.");
    }
}
