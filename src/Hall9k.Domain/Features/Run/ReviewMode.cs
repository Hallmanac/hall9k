using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The shape a review cycle's dispatch took (task: review cycles after the first). Cycle 1 always
/// discovers; a middle cycle verifies a fix instead of re-discovering the whole diff; the cycle
/// immediately before the pull request opens (or a follow-up pushes) always discovers again, once,
/// so nothing reaches the remote on delta-green alone.
/// <para>
/// <b>Discovery</b> is the shape review always had before this task: every still-active track gets
/// its own fresh-context pass over the whole diff (Decisions Log #59). Cycle 1 is always Discovery.
/// </para>
/// <para>
/// <b>Verify</b> collapses the two lenses into one reviewer for a middle cycle, handed the prior
/// cycle's own findings, each finding's fix position, and the commits added since that cycle —
/// verify-and-check-blast-radius rather than full rediscovery, because discovery already happened
/// once for this diff. Its rounds count against the same per-track caps a Discovery cycle's would
/// (<see cref="ReviewTrackOutcome"/> and the caps in <c>DaemonOptions</c> read the cycle number,
/// never this field), and a dispute or a cap-out parks exactly as it always has.
/// </para>
/// <para>
/// <b>FinalFullPass</b> is the one mandatory full-rigor read before the loop is allowed to call
/// itself done: both lenses, fresh context, whether or not a track had already gone dormant. A
/// track that finds nothing new here concludes again on this cycle's own terms; a track that finds
/// something is genuinely reawakened (<see cref="Events.ReviewTrackReactivated"/>) and the ordinary
/// fix-and-reverify machinery — caps included — decides what happens next.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewModeJsonConverter))]
public sealed record ReviewMode
{
    /// <summary>Every still-active track, fresh context, full diff — the shape review always had.</summary>
    public static readonly ReviewMode Discovery = new("Discovery");

    /// <summary>One reviewer, handed the prior findings and told to verify the fix and its blast radius.</summary>
    public static readonly ReviewMode Verify = new("Verify");

    /// <summary>The mandatory full-rigor read immediately before the run may settle.</summary>
    public static readonly ReviewMode FinalFullPass = new("FinalFullPass");

    public string Value { get; }

    private ReviewMode(string value) => Value = value;

    public static implicit operator string(ReviewMode? mode) => mode?.Value ?? string.Empty;

    public static implicit operator ReviewMode(string? value) => value.IsBlank() ? Discovery : Parse(value);

    /// <summary>Reads the stream's own word for the mode; anything unrecognized — including an old stream that never recorded one — reads as Discovery, the shape every cycle had before this field existed.</summary>
    public static ReviewMode Parse(string? value) => value?.Trim() switch
    {
        "Verify" => Verify,
        "FinalFullPass" => FinalFullPass,
        _ => Discovery,
    };

    public bool Equals(ReviewMode? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewModeJsonConverter : JsonConverter<ReviewMode>
    {
        public override ReviewMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, ReviewMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
