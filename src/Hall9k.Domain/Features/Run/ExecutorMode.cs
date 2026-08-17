using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>Billing/auth mode for a run. Owns the spawn-flag rules from Decisions Log #1
/// instead of leaving them to switch statements in the daemon.</summary>
[JsonConverter(typeof(ExecutorModeJsonConverter))]
public sealed record ExecutorMode
{
    public static readonly ExecutorMode Subscription = new("Subscription");
    public static readonly ExecutorMode ApiKey = new("ApiKey");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly ExecutorMode Unknown = new("");

    public string Value { get; }

    private ExecutorMode(string value) => Value = value;

    /// <summary>claude's --bare flag is API-key-only auth; never used in subscription mode.</summary>
    public bool UsesBareFlag => this == ApiKey;

    /// <summary>Whether the daemon injects ANTHROPIC_API_KEY into the executor environment.</summary>
    public bool InjectsApiKey => this == ApiKey;

    public static implicit operator string(ExecutorMode? value) => value?.Value ?? string.Empty;

    public static implicit operator ExecutorMode(string? value) => value.IsBlank() ? Unknown : new ExecutorMode(value);

    public bool Equals(ExecutorMode? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ExecutorModeJsonConverter : JsonConverter<ExecutorMode>
    {
        public override ExecutorMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ExecutorMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
