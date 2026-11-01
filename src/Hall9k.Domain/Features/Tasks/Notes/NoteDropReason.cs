using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks.Notes;

/// <summary>Why the closeout promotion step did not turn a note into a lesson, from a closed list.</summary>
[JsonConverter(typeof(NoteDropReasonJsonConverter))]
public sealed record NoteDropReason
{
    /// <summary>The default: the session did not choose the note, which is the answer for most of them.</summary>
    public static readonly NoteDropReason NotKept = new("not-kept");

    /// <summary>The session chose the note and its claim was blank.</summary>
    public static readonly NoteDropReason ClaimBlank = new("claim-blank");

    /// <summary>The session chose the note and its claim was longer than <see cref="NotePromotionLimits.MaxClaimLength"/>.</summary>
    public static readonly NoteDropReason ClaimTooLong = new("claim-too-long");

    /// <summary>The session chose the note and its claim held a control, format, private-use or unassigned character, which a person reading the lesson could not see.</summary>
    public static readonly NoteDropReason ClaimUnprintable = new("claim-unprintable");

    /// <summary>The note stood but another stood first, and a task promotes at most <see cref="NotePromotionLimits.MaxKept"/>.</summary>
    public static readonly NoteDropReason OverCeiling = new("over-ceiling");

    /// <summary>The step failed, so nothing was promoted whatever the note said.</summary>
    public static readonly NoteDropReason StepFailed = new("step-failed");

    /// <summary>The note was past the most the step shows its session, so the session never saw it.</summary>
    public static readonly NoteDropReason NotOffered = new("not-offered");

    public static readonly NoteDropReason Unknown = new("");

    /// <summary>The whole vocabulary, in the order a reader meets the reasons above.</summary>
    public static readonly IReadOnlyList<NoteDropReason> All =
        [NotKept, ClaimBlank, ClaimTooLong, ClaimUnprintable, OverCeiling, StepFailed, NotOffered];

    public string Value { get; }

    private NoteDropReason(string value) => Value = value;

    public static NoteDropReason FromValue(string? value) =>
        All.FirstOrDefault(reason => reason.Value == value) ?? Unknown;

    public static implicit operator string(NoteDropReason? value) => value?.Value ?? string.Empty;

    public static implicit operator NoteDropReason(string? value) => FromValue(value);

    public bool Equals(NoteDropReason? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class NoteDropReasonJsonConverter : JsonConverter<NoteDropReason>
    {
        public override NoteDropReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, NoteDropReason value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
