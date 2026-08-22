using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Whether a review finding is about this branch's own work or about code it merely sits
/// next to (Decisions Log #63). The anchor is mechanical on purpose, so the tag is checkable
/// against the diff rather than a judgment call: in-scope means the defective line lives in
/// code this branch added or changed, out-of-scope means the defect is pre-existing on the
/// base branch.
/// <para>
/// Scope decides routing, not importance. An out-of-scope High is still fixed in this pull
/// request (cleanup-as-you-touch), while an out-of-scope Medium or Low becomes a draft bug
/// task instead of growing the diff.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewFindingScopeJsonConverter))]
public sealed record ReviewFindingScope
{
    /// <summary>The defective line lives in code this branch added or changed.</summary>
    public static readonly ReviewFindingScope InScope = new("InScope");

    /// <summary>The defect is pre-existing on the base branch; this diff only revealed it.</summary>
    public static readonly ReviewFindingScope OutOfScope = new("OutOfScope");

    /// <summary>
    /// The reviewer stated no scope. Treated as in-scope everywhere it matters, which keeps the
    /// finding in this pull request instead of routing an untagged defect into a draft nobody
    /// asked for: routing is the irreversible half of the decision, so the untagged case takes
    /// the reversible one. Serializes as an empty string.
    /// </summary>
    public static readonly ReviewFindingScope Unknown = new("");

    public string Value { get; }

    private ReviewFindingScope(string value) => Value = value;

    /// <summary>Whether this finding may be routed away instead of fixed here. Only a stated out-of-scope tag qualifies.</summary>
    public bool IsRoutable => this == OutOfScope;

    /// <summary>Reads a reviewer's own word for the tag; anything unrecognized stays <see cref="Unknown"/>.</summary>
    public static ReviewFindingScope Parse(string? value) => value?.Trim().ToLowerInvariant().Replace("_", "-") switch
    {
        "in-scope" or "inscope" or "in scope" => InScope,
        "out-of-scope" or "outofscope" or "out of scope" or "out-of scope" => OutOfScope,
        _ => Unknown,
    };

    public static implicit operator string(ReviewFindingScope? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewFindingScope(string? value) =>
        value.IsBlank() ? Unknown : new ReviewFindingScope(value);

    public bool Equals(ReviewFindingScope? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewFindingScopeJsonConverter : JsonConverter<ReviewFindingScope>
    {
        public override ReviewFindingScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewFindingScope value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
