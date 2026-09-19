using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Who answers a cooperative claim request (idea 202383dc, item 5, "a member can ask a holder for
/// a task"): <see cref="Auto"/>, the default, has the holder's own node answer on receipt — it
/// grants when no run is live for the task, and refuses otherwise, naming the run's own start
/// time. <see cref="Ask"/> parks every request for the holder's own human instead, who answers
/// with <c>h9k task grant</c> or <c>h9k task refuse --reason</c>. Modelled on <see cref="ClaimGate"/>:
/// raw wrapping rather than validation, so <see cref="Handlers.ProjectDecider.ChangeSettings"/> is
/// the one place the closed set is actually enforced.
/// </summary>
[JsonConverter(typeof(TakePolicyJsonConverter))]
public sealed record TakePolicy
{
    /// <summary>The default: the holder's own node decides on receipt, no human involved unless a run is live.</summary>
    public static readonly TakePolicy Auto = new("Auto");

    /// <summary>Every claim request parks for the holder's own human — <c>h9k task grant</c>/<c>refuse</c> answers it.</summary>
    public static readonly TakePolicy Ask = new("Ask");

    public string Value { get; }

    private TakePolicy(string value) => Value = value;

    public static implicit operator string(TakePolicy? policy) => policy?.Value ?? Auto.Value;

    public static implicit operator TakePolicy(string? value) =>
        value.IsBlank() ? Auto : new TakePolicy(value);

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as Auto.</summary>
    public static TakePolicy FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "ask" => Ask,
        _ => Auto,
    };

    /// <summary>The strict form a human's own input goes through: a typo is refused, never silently read as auto.</summary>
    public static TakePolicy Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? Auto
            : FromInput(trimmed) is { } parsed && parsed != Auto
                ? parsed
                : throw new DomainValidationException($"'{trimmed}' is not a take policy. Use auto or ask.");
    }

    public bool Equals(TakePolicy? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class TakePolicyJsonConverter : JsonConverter<TakePolicy>
    {
        public override TakePolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, TakePolicy value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
