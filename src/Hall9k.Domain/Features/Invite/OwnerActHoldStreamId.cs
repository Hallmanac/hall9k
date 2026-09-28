using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Features.Invite;

/// <summary>
/// The deterministic Marten stream id for one owner-act hold, mirroring
/// <c>Hall9k.Domain.Features.Message.MessageStreamId</c>'s own reasoning: never the owner-act-request
/// message's own <c>MessageAggregate</c> stream id directly (<see cref="ForRequest"/>'s own
/// <paramref name="requestMessageId"/>), which already addresses that message's own stream — starting
/// a second stream under the identical Guid would collide with it rather than open a new one. A SHA-256
/// hash of a tagged key, taken sixteen bytes wide, so the identical request always resolves to the
/// identical hold.
/// </summary>
public static class OwnerActHoldStreamId
{
    public static Guid ForRequest(Guid requestMessageId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"hall9k-owner-act-hold|{requestMessageId:N}"));
        return new Guid(hash[..16]);
    }
}
