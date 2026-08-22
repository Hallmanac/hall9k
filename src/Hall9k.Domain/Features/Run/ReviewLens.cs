using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// Which attention budget produced a pre-PR review pass (Decisions Log #59). One review
/// pass is one sample: a reviewer looks for what it was told to look for, and a defect
/// outside that attention budget is not found however capable the reviewer is. The lenses
/// turn that sampling property into the mechanism — Conformance asks whether the work
/// meets its objective, its acceptance criteria, and repo doctrine; Adversarial assumes
/// the code is wrong somewhere and hunts defect classes without being told what the work
/// was supposed to do.
/// <para>
/// A lens is also a <em>track</em> (Decisions Log #63): each one converges on its own terms
/// and drops out of the loop on its own. Conformance ends the moment it comes back clean and
/// parks the run if it is still finding things after its cycle cap; Adversarial keeps going
/// under the severity gate up to its much larger cap. A track that ends goes dormant and the
/// other continues alone, so "the lens list" is the set of tracks a cycle dispatches, minus
/// whichever have already finished.
/// </para>
/// <para>
/// Origin incident (2026-08-21, PR #21): four Copilot passes over the same branch each
/// surfaced different findings, including an injection risk that had been present in all
/// four reviewed commits — the passes were re-sampling the same code, not reacting to new
/// defects, and the accumulated samples caught what a single internal conformance review
/// had missed across several cycles.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewLensJsonConverter))]
public sealed record ReviewLens
{
    /// <summary>Does the work meet the objective, the acceptance criteria, and repo doctrine?</summary>
    public static readonly ReviewLens Conformance = new("Conformance");

    /// <summary>Where is this code wrong, regardless of what it was asked to do?</summary>
    public static readonly ReviewLens Adversarial = new("Adversarial");

    /// <summary>
    /// Not recognized, or a review pass recorded before lenses existed — that pass was the
    /// conformance reviewer, since it was the only one there was, but it never said so on
    /// the stream and this sentinel does not put words in its mouth. Serializes as an empty string.
    /// </summary>
    public static readonly ReviewLens Unknown = new("");

    /// <summary>
    /// The tracks a review opens with, in dispatch order. This list is the seam: a third
    /// lens (or a repeated adversarial sample) arrives by appending here, and the engine, the
    /// aggregate, and the artifact layout already handle an arbitrary count. Two is the
    /// shipped shape because two is what the origin incident justified.
    /// <para>
    /// It is the <em>opening</em> set, not the per-cycle set: once a track concludes it stops
    /// being dispatched, and later cycles run only whichever tracks are still active.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<ReviewLens> CycleLenses = [Conformance, Adversarial];

    public string Value { get; }

    /// <summary>
    /// Lowercase form used in artifact file names. Blank for <see cref="Unknown"/>, which is
    /// what keeps a pre-lens run's artifacts findable under the names they were written with.
    /// </summary>
    public string Slug => Value.ToLowerInvariant();

    private ReviewLens(string value) => Value = value;

    /// <summary>
    /// Whether a pass recorded under this lens accounts for <paramref name="lens"/> in its
    /// cycle. A lens accounts for itself, and a pass recorded without a lens accounts for
    /// Conformance: a run dispatched before lenses existed ran exactly one reviewer and that
    /// reviewer was the conformance reviewer — what shipped, not a guess about what it did.
    /// </summary>
    public bool Covers(ReviewLens lens) => this == lens || (this == Unknown && lens == Conformance);

    /// <summary>
    /// The lenses of <paramref name="required"/> that none of <paramref name="recorded"/>
    /// accounts for. A cycle with anything missing here has not concluded, however many passes
    /// have answered: a verdict reached without a lens is a lens that never looked, not a lens
    /// that found nothing.
    /// <para>
    /// <paramref name="required"/> is the run's still-active tracks (Decisions Log #63), not
    /// <see cref="CycleLenses"/>: a track that already concluded is not missing from the cycle,
    /// it is finished with the run.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReviewLens> MissingFrom(
        IEnumerable<ReviewLens> required, IEnumerable<ReviewLens> recorded)
    {
        List<ReviewLens> present = [.. recorded];
        return [.. required.Where(lens => !present.Any(pass => pass.Covers(lens)))];
    }

    public static implicit operator string(ReviewLens? lens) => lens?.Value ?? string.Empty;

    public static implicit operator ReviewLens(string? value) => value.IsBlank() ? Unknown : new ReviewLens(value);

    public bool Equals(ReviewLens? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewLensJsonConverter : JsonConverter<ReviewLens>
    {
        public override ReviewLens Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewLens value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
