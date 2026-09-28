using System.Text.Json;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// The wire shapes for the two owner-act envelope kinds (idea 6be68ee2, companion 1bb803e1) —
/// <see cref="MessageKind.OwnerActRequest"/> and <see cref="MessageKind.OwnerActOutcome"/> — the
/// same plain-JSON-record convention <see cref="ClaimEnvelopeCodec"/> already uses for a payload
/// that is never user-visible prose. A decode failure returns null rather than throwing: a
/// malformed or foreign-build body is read the same way an unrecognized envelope kind is — skipped,
/// never a reason to refuse the whole sweep.
/// </summary>
public static class OwnerActEnvelopeCodec
{
    /// <param name="InviteId">The invite whose own match produced this write — the correlation key both sides key their own outstanding-request lookups on.</param>
    /// <param name="RequesterNodeId">The asking node — where the reply is addressed. Checked against the envelope's own authenticated sender before anything here is trusted (the identical stale/misdirected guard <see cref="ClaimEnvelopeCodec.ClaimRequestRecord.RequesterNodeId"/> already gets).</param>
    /// <param name="CandidateOwnerFingerprint">The new member's own root fingerprint — the <c>root_fingerprint</c> field the write itself carries.</param>
    /// <param name="Role">The new member's own role.</param>
    /// <param name="IssuedAt">
    /// The vouch's own <c>issued_at</c>, computed once by the requester and carried on every resend
    /// unchanged — never recomputed at write time — so a re-sent request reproduces byte-identical
    /// content and the root's own write is a costless no-op on retry (the sweep's adopt-issued_at
    /// pattern, <see cref="Hall9k.Daemon.Invites.InviteSweepEngine"/>).
    /// </param>
    public sealed record OwnerActRequestRecord(
        Guid InviteId, Guid RequesterNodeId, string CandidateOwnerFingerprint, ProjectMemberRole Role,
        DateTimeOffset IssuedAt);

    /// <summary>The closed set of verdicts an <see cref="OwnerActOutcomeRecord"/> ever carries.</summary>
    public static class OwnerActVerdict
    {
        /// <summary>The write landed — <see cref="OwnerActOutcomeRecord.CommitId"/> names the commit.</summary>
        public const string Done = "done";

        /// <summary>An owner-role write was parked on the root for its own human to approve — nothing written yet.</summary>
        public const string Held = "held";

        /// <summary>Refused — <see cref="OwnerActOutcomeRecord.Reason"/> says why.</summary>
        public const string Refused = "refused";

        /// <summary>The request sat unanswered past its own wait and the root gave up on it.</summary>
        public const string Expired = "expired";
    }

    public sealed record OwnerActOutcomeRecord(Guid InviteId, string Verdict, string? CommitId, string? Reason);

    public static string Encode(OwnerActRequestRecord record) => JsonSerializer.Serialize(record);

    public static string Encode(OwnerActOutcomeRecord record) => JsonSerializer.Serialize(record);

    public static OwnerActRequestRecord? TryDecodeRequest(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<OwnerActRequestRecord>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static OwnerActOutcomeRecord? TryDecodeOutcome(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<OwnerActOutcomeRecord>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
