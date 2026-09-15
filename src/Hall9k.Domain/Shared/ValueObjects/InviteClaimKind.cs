using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// What a minted invite lets its holder claim (idea 202383dc, T2): a new node of the minting
/// node's own owner, or a new member of the minting node's own project. Value object per the house
/// type discipline (TASK-MODEL.md §8): the closed set is defined once, serializes as its bare
/// lowercase-with-hyphens string — the same string <c>owners/&lt;root&gt;/invites/&lt;id&gt;.yaml</c>
/// carries in its own <c>claim</c> field — and an unrecognized value round-trips as itself rather
/// than failing (the <see cref="ProjectMemberRole"/> discipline, not <see cref="CommitStyle"/>'s
/// Unknown-clears-the-override one: an invite's claim is never a layered setting).
/// </summary>
[JsonConverter(typeof(InviteClaimKindJsonConverter))]
public sealed record InviteClaimKind
{
    /// <summary>A new node of the minting node's own owner — "any enrolled node invites its own
    /// owner's nodes" (idea 202383dc, A2b's ruling). Creates no project membership and no root.</summary>
    public static readonly InviteClaimKind NodeOfOwner = new("node-of-owner");

    /// <summary>A new member of the minting node's own project, owner role only — "owner role
    /// invites members" (idea 202383dc's ruling). Never touches the minting node's own owner chain.</summary>
    public static readonly InviteClaimKind MemberOfProject = new("member-of-project");

    /// <summary>Not recognized. Serializes as an empty string.</summary>
    public static readonly InviteClaimKind Unknown = new("");

    public string Value { get; }

    private InviteClaimKind(string value) => Value = value;

    public static implicit operator string(InviteClaimKind? claim) => claim?.Value ?? string.Empty;

    public static implicit operator InviteClaimKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "node-of-owner" => NodeOfOwner,
        "member-of-project" => MemberOfProject,
        _ => Unknown,
    };

    public bool Equals(InviteClaimKind? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class InviteClaimKindJsonConverter : JsonConverter<InviteClaimKind>
    {
        public override InviteClaimKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, InviteClaimKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
