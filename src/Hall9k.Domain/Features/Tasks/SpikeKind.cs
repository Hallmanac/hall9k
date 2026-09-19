using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// A spike's own kind (task: a spike is a run, not a walk): the two exit switches — whether the
/// build and test gates run, and whether the branch is kept — are driven entirely by which of
/// these three a spike is, never by separate configuration. Research reads and measures with no
/// code, so its deliverable is the transcript and findings, not a diff: no gates, branch kept
/// locally as the record. Experiment runs and measures, code discarded: no gates (nobody is
/// building anything durable), branch deleted locally once its findings are copied out. Prototype
/// builds enough to demonstrate: it must actually build and run, so it is the one kind that gets
/// the ordinary build and test gates, and its branch is pushed to origin so a later seeded task or
/// another node can reach it as evidence.
/// </summary>
[JsonConverter(typeof(SpikeKindJsonConverter))]
public sealed record SpikeKind
{
    public static readonly SpikeKind Research = new("Research");
    public static readonly SpikeKind Experiment = new("Experiment");
    public static readonly SpikeKind Prototype = new("Prototype");
    /// <summary>Not recognized or not yet set — the only value a non-spike task ever carries.</summary>
    public static readonly SpikeKind Unknown = new("");

    public string Value { get; }

    private SpikeKind(string value) => Value = value;

    /// <summary>Only Prototype must actually build and run — it is the one kind the build and test gates apply to.</summary>
    public bool RunsGates => this == Prototype;

    /// <summary>Research and Prototype both keep their branch as evidence; Experiment's is discarded.</summary>
    public bool KeepsBranch => this == Research || this == Prototype;

    /// <summary>Only Prototype's branch is pushed to origin, so a seeded task or another node can reach it.</summary>
    public bool PushesBranch => this == Prototype;

    public static implicit operator string(SpikeKind? value) => value?.Value ?? string.Empty;

    public static implicit operator SpikeKind(string? value) => value.IsBlank() ? Unknown : new SpikeKind(value);

    /// <summary>The CLI vocabulary. Refuses an unknown word with the choices quoted rather than guessing.</summary>
    public static SpikeKind Parse(string? value) =>
        TryParse(value, out SpikeKind? parsed)
            ? parsed
            : throw new DomainValidationException(
                $"Unknown spike kind '{value}'. Use research, experiment, or prototype.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out SpikeKind? parsed)
    {
        parsed = value?.Trim().ToLowerInvariant() switch
        {
            "research" => Research,
            "experiment" => Experiment,
            "prototype" => Prototype,
            _ => null,
        };

        return parsed is not null;
    }

    public bool Equals(SpikeKind? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class SpikeKindJsonConverter : JsonConverter<SpikeKind>
    {
        public override SpikeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, SpikeKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
