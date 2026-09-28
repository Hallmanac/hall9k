using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Whether a public repository's review request or mention needs hall9k team membership before it
/// runs unattended (security review idea 6be68ee2, finding 1), as <c>h9k project set
/// --review-requires-membership</c> records it. <see cref="Enabled"/> and <see cref="Disabled"/>
/// are explicit overrides of the daemon's own visibility-computed default; <see cref="Unknown"/> is
/// both the untouched default and what <c>default</c> restores — the
/// <see cref="ReviewRerequestPolicy"/> convention, kept a separate type because the two settings
/// resolve differently: this one has no owner-level tier to fall through to, only the project's own
/// recorded choice or the daemon's own fresh <c>gh repo view --json isPrivate</c> read.
/// </summary>
[JsonConverter(typeof(ReviewMembershipPolicyJsonConverter))]
public sealed record ReviewMembershipPolicy
{
    /// <summary>Membership is required whatever the repository's own visibility reads as.</summary>
    public static readonly ReviewMembershipPolicy Enabled = new("Enabled");

    /// <summary>Membership is never required here; every request or mention runs exactly as today's collaborator behaviour does.</summary>
    public static readonly ReviewMembershipPolicy Disabled = new("Disabled");

    /// <summary>Not recorded, or explicitly cleared back to the daemon's own visibility-computed default. Serializes as an empty string.</summary>
    public static readonly ReviewMembershipPolicy Unknown = new("");

    public string Value { get; }

    private ReviewMembershipPolicy(string value) => Value = value;

    public static implicit operator string(ReviewMembershipPolicy? policy) => policy?.Value ?? string.Empty;

    public static implicit operator ReviewMembershipPolicy(string? value) =>
        value.IsBlank() ? Unknown : new ReviewMembershipPolicy(value);

    /// <summary>Maps a stored value back to the closed set; unrecognised reads as <see cref="Unknown"/>.</summary>
    public static ReviewMembershipPolicy FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "enabled" or "on" or "true" or "yes" => Enabled,
        "disabled" or "off" or "false" or "no" => Disabled,
        _ => Unknown,
    };

    public bool Equals(ReviewMembershipPolicy? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewMembershipPolicyJsonConverter : JsonConverter<ReviewMembershipPolicy>
    {
        public override ReviewMembershipPolicy Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewMembershipPolicy value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
