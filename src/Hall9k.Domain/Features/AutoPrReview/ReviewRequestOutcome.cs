using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.AutoPrReview;

/// <summary>
/// What this install actually did about one review request GitHub made of its own login
/// (Decisions Log #161) — the outcome attached to every <see cref="ObservedReviewRequest"/>, so
/// the record says not only that the request was seen but what became of it.
/// <para>
/// A closed vocabulary in the TASK-MODEL.md §8 shape (sealed record, static instances, an
/// <see cref="Unknown"/> sentinel) rather than an enum, because it is persisted: a row written
/// by a later build carrying an outcome this one has never heard of reads back as
/// <see cref="Unknown"/> and is described as unrecognised, never silently mapped onto a
/// neighbouring value that would make a different claim about what happened.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewRequestOutcomeJsonConverter))]
public sealed record ReviewRequestOutcome
{
    /// <summary>An outcome this build does not recognise — a row written by a newer one.</summary>
    public static readonly ReviewRequestOutcome Unknown = new("Unknown");

    /// <summary>A pr-review task was minted, published and assigned for this request.</summary>
    public static readonly ReviewRequestOutcome TaskCreated = new("TaskCreated");

    /// <summary>A task already covered this pull request — auto-created earlier, or a human's own <c>h9k task add --from-pr</c>.</summary>
    public static readonly ReviewRequestOutcome AlreadyCovered = new("AlreadyCovered");

    /// <summary>Nothing was minted: this project explicitly recorded <c>--auto-pr-review off</c>.</summary>
    public static readonly ReviewRequestOutcome HeldSettingOff = new("HeldSettingOff");

    /// <summary>Nothing was minted: the request predates this project's own cutoff (the no-backfill guard).</summary>
    public static readonly ReviewRequestOutcome HeldBeforeCutoff = new("HeldBeforeCutoff");

    /// <summary>Nothing was minted: GitHub's own requested-at time could not be read, so nothing proves the request is new.</summary>
    public static readonly ReviewRequestOutcome HeldRequestTimeUnknown = new("HeldRequestTimeUnknown");

    /// <summary>The mint was attempted and refused — a pull request that could not be imported, a race with GitHub itself.</summary>
    public static readonly ReviewRequestOutcome MintFailed = new("MintFailed");

    public string Value { get; }

    private ReviewRequestOutcome(string value) => Value = value;

    // Deliberately no "does this outcome ask the operator?" property here (independent pre-PR
    // review, cycle 1, both lenses, low). A stored outcome cannot answer it: whether a row needs
    // the operator also depends on facts read at render time — whether a task covers the pull
    // request now, and what the project's setting says now — and an earlier cut of this type
    // carried one that already disagreed with ReviewRequestPane.Compose about an unrecognised
    // outcome while nothing in the solution called it. Needs-you lives in that one place, so two
    // surfaces cannot answer it differently.

    public static implicit operator string(ReviewRequestOutcome? outcome) => outcome?.Value ?? Unknown.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="Project.AutoPrReviewSpeed"/> convention:
    /// nothing takes this value off a command line, and the recognised set is what
    /// <see cref="FromInput"/> maps a stored value back through.
    /// </summary>
    public static implicit operator ReviewRequestOutcome(string? value) =>
        value.IsBlank() ? Unknown : new ReviewRequestOutcome(value);

    /// <summary>Lenient mapping for a value already on a stored row; unrecognised reads as <see cref="Unknown"/>.</summary>
    public static ReviewRequestOutcome FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "taskcreated" => TaskCreated,
        "alreadycovered" => AlreadyCovered,
        "heldsettingoff" => HeldSettingOff,
        "heldbeforecutoff" => HeldBeforeCutoff,
        "heldrequesttimeunknown" => HeldRequestTimeUnknown,
        "mintfailed" => MintFailed,
        _ => Unknown,
    };

    public bool Equals(ReviewRequestOutcome? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewRequestOutcomeJsonConverter : JsonConverter<ReviewRequestOutcome>
    {
        public override ReviewRequestOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewRequestOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
