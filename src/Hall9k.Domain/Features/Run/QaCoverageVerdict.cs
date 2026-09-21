using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What a QA review owes one entry on its blast-radius map (idea b9b09779, piece 2): the change
/// is already covered, it needs a new automated end-to-end test, or it needs a person to walk it
/// by hand. Exactly three answers, closed on purpose — the value of the map is that no affected
/// behaviour is left without one of them, and a fourth answer would be somewhere for "it depends"
/// to hide.
/// <para>
/// A sealed record with static instances and an <see cref="Unstated"/> sentinel per the house
/// type discipline (TASK-MODEL.md §8) rather than an enum, on the same terms
/// <see cref="RunSkillDriftAnswer"/> states: this is read off an agent's own written report, so
/// "the reviewer did not say" has to be representable and must never be filled in with the
/// nearest plausible answer.
/// </para>
/// </summary>
[JsonConverter(typeof(QaCoverageVerdictJsonConverter))]
public sealed record QaCoverageVerdict
{
    /// <summary>An automated test already exercises this, and the report names it.</summary>
    public static readonly QaCoverageVerdict Covered = new("covered");

    /// <summary>Nothing covers this yet; the report specifies the automated end-to-end test that should.</summary>
    public static readonly QaCoverageVerdict NewTest = new("new-test");

    /// <summary>No automated test can honestly answer this; the report writes out the steps a person walks.</summary>
    public static readonly QaCoverageVerdict WalkThrough = new("walk-through");

    /// <summary>
    /// The entry carries no readable verdict. Distinct from every real answer on purpose: an
    /// entry nobody graded is the exact gap the map exists to make visible, and guessing at it
    /// would hide the gap rather than report it. Serializes as the empty string.
    /// </summary>
    public static readonly QaCoverageVerdict Unstated = new("");

    /// <summary>The three real answers, in the order a report lists them.</summary>
    public static readonly IReadOnlyList<QaCoverageVerdict> All = [Covered, NewTest, WalkThrough];

    public string Value { get; }

    private QaCoverageVerdict(string value) => Value = value;

    /// <summary>True for one of the three; false for <see cref="Unstated"/>.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>The tolerant read — anything else is <see cref="Unstated"/>, never a guess.</summary>
    public static QaCoverageVerdict Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "covered" => Covered,
        "new-test" or "new test" or "needs-new-test" => NewTest,
        "walk-through" or "walkthrough" or "walk through" or "human-walk-through" => WalkThrough,
        _ => Unstated,
    };

    /// <summary>How the verdict reads in a findings report, so every report words it identically.</summary>
    public string Describe() =>
        this == Covered ? "covered by an existing automated test"
        : this == NewTest ? "needs a new automated end-to-end test"
        : this == WalkThrough ? "needs a human walk-through"
        : "no verdict stated";

    public static implicit operator string(QaCoverageVerdict? value) => value?.Value ?? string.Empty;

    public bool Equals(QaCoverageVerdict? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class QaCoverageVerdictJsonConverter : JsonConverter<QaCoverageVerdict>
    {
        public override QaCoverageVerdict Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, QaCoverageVerdict value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
