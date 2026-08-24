using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Why a done task was reopened for a follow-up run (PR closeout, Decisions Log #18/#22).
/// The launcher selects the agent prompt from it: ReviewFeedback gets the
/// resolve-review-threads prompt, FailingChecks gets the fix-the-CI prompt, Rebase gets the
/// rebase-onto-main prompt (backlog 44). Unknown (including reopens recorded before this
/// vocabulary existed) is treated as ReviewFeedback.
/// </summary>
[JsonConverter(typeof(FollowUpKindJsonConverter))]
public sealed record FollowUpKind
{
    public static readonly FollowUpKind ReviewFeedback = new("ReviewFeedback");
    public static readonly FollowUpKind FailingChecks = new("FailingChecks");
    /// <summary>The pull request's branch conflicts with its base; the follow-up rebases it (backlog 44).</summary>
    public static readonly FollowUpKind Rebase = new("Rebase");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly FollowUpKind Unknown = new("");

    public string Value { get; }

    private FollowUpKind(string value) => Value = value;

    public static implicit operator string(FollowUpKind? value) => value?.Value ?? string.Empty;

    public static implicit operator FollowUpKind(string? value) => value.IsBlank() ? Unknown : new FollowUpKind(value);

    public bool Equals(FollowUpKind? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class FollowUpKindJsonConverter : JsonConverter<FollowUpKind>
    {
        public override FollowUpKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, FollowUpKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
