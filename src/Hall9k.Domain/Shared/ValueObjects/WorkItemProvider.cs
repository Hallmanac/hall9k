using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The external system a work item or connection belongs to. Value object per the house
/// type discipline (TASK-MODEL.md §8): the closed set is defined once, serializes as its
/// bare string, and unrecognized values round-trip as themselves rather than failing.
/// </summary>
[JsonConverter(typeof(WorkItemProviderJsonConverter))]
public sealed record WorkItemProvider
{
    public static readonly WorkItemProvider GitHub = new("github");
    public static readonly WorkItemProvider Jira = new("jira");

    /// <summary>Provider not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly WorkItemProvider Unknown = new("");

    public string Value { get; }

    private WorkItemProvider(string value) => Value = value;

    public static implicit operator string(WorkItemProvider? provider) => provider?.Value ?? string.Empty;

    public static implicit operator WorkItemProvider(string? value) => value.IsBlank() ? Unknown : new WorkItemProvider(value);

    public bool Equals(WorkItemProvider? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class WorkItemProviderJsonConverter : JsonConverter<WorkItemProvider>
    {
        public override WorkItemProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, WorkItemProvider value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
