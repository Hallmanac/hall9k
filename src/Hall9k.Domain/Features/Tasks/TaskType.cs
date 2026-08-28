using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>Drives persona, prompt template, and verification profile (PLAN.md §4).</summary>
[JsonConverter(typeof(TaskTypeJsonConverter))]
public sealed record TaskType
{
    public static readonly TaskType Feature = new("Feature");
    public static readonly TaskType Bugfix = new("Bugfix");
    public static readonly TaskType Refactor = new("Refactor");
    public static readonly TaskType Chore = new("Chore");
    public static readonly TaskType Research = new("Research");
    /// <summary>
    /// Reviews another contributor's pull request on the owner's behalf: read-only, no fix
    /// loop, no merge to watch for — the deliverable is a findings report the owner directs
    /// (dismiss, comment themselves, or have the session post on their behalf), never a diff.
    /// </summary>
    public static readonly TaskType PrReview = new("PrReview");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskType Unknown = new("");

    public string Value { get; }

    private TaskType(string value) => Value = value;

    public static implicit operator string(TaskType? value) => value?.Value ?? string.Empty;

    public static implicit operator TaskType(string? value) => value.IsBlank() ? Unknown : new TaskType(value);

    /// <summary>
    /// The CLI vocabulary, spelled as a human types it. Refuses an unknown word with the
    /// choices quoted rather than quietly recording Unknown — a task type drives persona,
    /// prompt template, and verification profile, so a typo must not slip through as nothing.
    /// A blank input is the documented default rather than an error: Feature.
    /// </summary>
    public static TaskType Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "feature" => Feature,
        "bugfix" or "bug" => Bugfix,
        "refactor" => Refactor,
        "chore" => Chore,
        "research" => Research,
        "pr-review" or "pr_review" or "prreview" => PrReview,
        _ => throw new DomainValidationException(
            $"Unknown task type '{value}'. Use feature, bugfix, refactor, chore, research, or pr-review."),
    };

    public bool Equals(TaskType? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class TaskTypeJsonConverter : JsonConverter<TaskType>
    {
        public override TaskType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, TaskType value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
