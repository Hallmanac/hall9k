using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// How far a recorded decision or lesson reaches (idea d805fd8b, piece 1): the two scopes the
/// store ships with, and the sentinel for a value it does not recognise. A closed vocabulary, so a
/// sealed record with static instances rather than an enum (TASK-MODEL.md §8) — a third scope
/// (per task type, per persona: PLAN.md §6.6, IDEA-learning-capture's own open question) is a
/// static instance and a query clause here, never a schema change.
/// <para>
/// Scope is not filing convenience, it decides travel. <see cref="Project"/> is the default and
/// the deliberate act is widening to <see cref="Owner"/>, because the failure is asymmetric: too
/// narrow means one other project misses something useful, too wide means a wrong statement rides
/// in every prompt on every project (IDEA-learning-capture, "Scope determines travel").
/// </para>
/// </summary>
[JsonConverter(typeof(KnowledgeScopeJsonConverter))]
public sealed record KnowledgeScope
{
    /// <summary>This project's own decision or lesson. Travels with the project once M2a replication carries it.</summary>
    public static readonly KnowledgeScope Project = new("Project");

    /// <summary>A cross-project habit of this owner's. Stays on the node that recorded it for now (idea d805fd8b, piece 1).</summary>
    public static readonly KnowledgeScope Owner = new("Owner");

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly KnowledgeScope Unknown = new("");

    public string Value { get; }

    private KnowledgeScope(string value) => Value = value;

    public static implicit operator string(KnowledgeScope? scope) => scope?.Value ?? string.Empty;

    public static implicit operator KnowledgeScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unknown : new KnowledgeScope(value);

    public bool Equals(KnowledgeScope? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class KnowledgeScopeJsonConverter : JsonConverter<KnowledgeScope>
    {
        public override KnowledgeScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, KnowledgeScope value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
