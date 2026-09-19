using System.Text.Json;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// The wire shapes for the three cooperative-take envelope kinds (idea 202383dc, item 5) —
/// <see cref="MessageKind.ClaimRequest"/>, <see cref="MessageKind.ClaimGranted"/>, and
/// <see cref="MessageKind.ClaimRefused"/> — the same plain-JSON-record convention
/// <c>Hall9k.Domain.Features.Replication.EventReplicationCodec</c> already uses for a payload
/// that is never user-visible prose. A decode failure returns null rather than throwing: a
/// malformed or foreign-build body is read the same way an unrecognized envelope kind is —
/// skipped, never a reason to refuse the whole sweep.
/// </summary>
public static class ClaimEnvelopeCodec
{
    /// <param name="TaskId">The task being asked for.</param>
    /// <param name="RequesterNodeId">The asking node — where the reply is addressed.</param>
    /// <param name="RequesterOwnerId">The asking node's own local owner id (never carried across nodes as an identity — <paramref name="RequesterOwnerFingerprint"/> is).</param>
    /// <param name="RequesterOwnerFingerprint">The asking owner's cross-node root fingerprint.</param>
    /// <param name="Reason">Why the requester wants this task — required, the same as <c>h9k task take --force --reason</c>.</param>
    /// <param name="RequesterTrackerIdentity">
    /// The requester's own tracker identity (a Jira accountId or a GitHub login), read locally on
    /// the requester's own node before sending, or null when this project is not gated or the
    /// requester has no tracker connection configured. Carried here because the granting node has
    /// no other way to learn it: every teammate's tracker credentials are local to their own
    /// install (<see cref="Project.ClaimGate"/>'s own doc).
    /// </param>
    public sealed record ClaimRequestRecord(
        Guid TaskId, Guid RequesterNodeId, Guid RequesterOwnerId, string RequesterOwnerFingerprint, string Reason,
        string? RequesterTrackerIdentity);

    public sealed record ClaimGrantedRecord(Guid TaskId);

    public sealed record ClaimRefusedRecord(Guid TaskId, string Reason);

    public static string Encode(ClaimRequestRecord record) => JsonSerializer.Serialize(record);

    public static string Encode(ClaimGrantedRecord record) => JsonSerializer.Serialize(record);

    public static string Encode(ClaimRefusedRecord record) => JsonSerializer.Serialize(record);

    public static ClaimRequestRecord? TryDecodeRequest(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ClaimRequestRecord>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ClaimGrantedRecord? TryDecodeGranted(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ClaimGrantedRecord>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ClaimRefusedRecord? TryDecodeRefused(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ClaimRefusedRecord>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
