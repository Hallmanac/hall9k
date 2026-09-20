using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// How far an idea's or a task's own events travel (idea 8c5993c5): <see cref="Private"/> never
/// leaves this node, <see cref="Fleet"/> reaches every node this same owner runs (addressed to
/// <c>owner:&lt;fingerprint&gt;</c>, the addressing an invite proof already uses), and
/// <see cref="Team"/> reaches every project member's own fleet (addressed to the project). Ordered
/// low to high in exactly that sequence — <see cref="IsAtLeast"/> is what the one-way team rule
/// ("team is one-way, so once other members hold a copy the scope never goes back below team")
/// checks before a command is allowed to move a currently-<see cref="Team"/> item to anything else.
/// </summary>
[JsonConverter(typeof(ReplicationScopeJsonConverter))]
public sealed record ReplicationScope
{
    public static readonly ReplicationScope Private = new("Private", 0);
    public static readonly ReplicationScope Fleet = new("Fleet", 1);
    public static readonly ReplicationScope Team = new("Team", 2);

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly ReplicationScope Unknown = new("", -1);

    public string Value { get; }

    private readonly int rank;

    private ReplicationScope(string value, int rank)
    {
        Value = value;
        this.rank = rank;
    }

    public static implicit operator string(ReplicationScope? scope) => scope?.Value ?? string.Empty;

    public static implicit operator ReplicationScope(string? value) => value switch
    {
        null or "" => Unknown,
        nameof(Private) => Private,
        nameof(Fleet) => Fleet,
        nameof(Team) => Team,
        _ => new ReplicationScope(value, -1),
    };

    /// <summary>Whether this scope reaches at least as far as <paramref name="floor"/> — the one-way team check's own comparison.</summary>
    public bool IsAtLeast(ReplicationScope floor) => rank >= floor.rank;

    public bool Equals(ReplicationScope? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReplicationScopeJsonConverter : JsonConverter<ReplicationScope>
    {
        public override ReplicationScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReplicationScope value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
