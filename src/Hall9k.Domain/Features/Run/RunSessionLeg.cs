using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which of a run's sessions an error-result retry (task: a session that reports an error
/// result is retried once in place) applies to — the same primary-session-versus-review-loop
/// split <c>TokenBudgetRetryEngine</c> already draws for token-budget recovery
/// (<see cref="Events.RunBudgetExhausted"/>), named here so <see cref="Events.RunSessionErrorRetried"/>
/// can say which leg without a reader rederiving it from <see cref="ReviewPhase"/>.
/// </summary>
[JsonConverter(typeof(RunSessionLegJsonConverter))]
public sealed record RunSessionLeg
{
    /// <summary>The primary agent session that writes the feature.</summary>
    public static readonly RunSessionLeg Build = new("Build");

    /// <summary>One review pass of the pre-PR loop (Decisions Log #59) — <see cref="Events.RunSessionErrorRetried.Lens"/> says which.</summary>
    public static readonly RunSessionLeg ReviewPass = new("ReviewPass");

    /// <summary>The session that applies review findings in the run's worktree.</summary>
    public static readonly RunSessionLeg Fix = new("Fix");

    /// <summary>Not recognized. Serializes as an empty string.</summary>
    public static readonly RunSessionLeg Unknown = new("");

    public string Value { get; }

    private RunSessionLeg(string value) => Value = value;

    public static implicit operator string(RunSessionLeg? leg) => leg?.Value ?? string.Empty;

    public static implicit operator RunSessionLeg(string? value) => value.IsBlank() ? Unknown : new RunSessionLeg(value);

    public bool Equals(RunSessionLeg? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunSessionLegJsonConverter : JsonConverter<RunSessionLeg>
    {
        public override RunSessionLeg Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, RunSessionLeg value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
