using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What happened when a QA review ran the project's end-to-end tests on the review worktree
/// (idea b9b09779, piece 2). Four observations and a sentinel for "the session never said",
/// because all five are different facts and only four of them are observations.
/// <para>
/// <see cref="Absent"/> is the one worth spelling out: a project with no end-to-end suite to run
/// is not a passing one, and a report that rendered the two identically would be claiming a
/// green run nobody ever saw. It is also, on its own, the most useful thing a QA review can tell
/// a team that thinks it has coverage.
/// </para>
/// <para>
/// <see cref="Unaccepted"/> is the one that is not purely the session's own observation: it names
/// a fact about this node (whether its operator has accepted the project's current gate set) that
/// the platform can check independently of what the session wrote, and <see cref="Describe"/>
/// does exactly that rather than repeating the session's word for it unchecked.
/// </para>
/// </summary>
[JsonConverter(typeof(QaEndToEndOutcomeJsonConverter))]
public sealed record QaEndToEndOutcome
{
    /// <summary>The end-to-end tests ran on this worktree and passed.</summary>
    public static readonly QaEndToEndOutcome Pass = new("pass");

    /// <summary>They ran and something failed. The report carries the failure's own output as evidence.</summary>
    public static readonly QaEndToEndOutcome Fail = new("fail");

    /// <summary>This project has no end-to-end tests to run, so nothing was observed either way.</summary>
    public static readonly QaEndToEndOutcome Absent = new("absent");

    /// <summary>
    /// This project's verify gate set has changed and is not yet accepted on this node (security
    /// review idea 6be68ee2, process-injection finding 1, the local half), so the QA review
    /// prompt withheld the gate commands and told the session not to run them, or to invent a
    /// substitute, itself — a different fact from <see cref="Absent"/>: the project has tests,
    /// this node simply could not hand the session a vetted command to run them with.
    /// </summary>
    public static readonly QaEndToEndOutcome Unaccepted = new("unaccepted");

    /// <summary>The session never answered — no marker, or a word nobody could read. Serializes as the empty string.</summary>
    public static readonly QaEndToEndOutcome Unstated = new("");

    public string Value { get; }

    private QaEndToEndOutcome(string value) => Value = value;

    /// <summary>True when the session actually answered.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>The tolerant read — anything else is <see cref="Unstated"/>, never a guess.</summary>
    public static QaEndToEndOutcome Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" or "passing" => Pass,
        "fail" or "failed" or "failing" => Fail,
        "absent" or "none" => Absent,
        "unaccepted" => Unaccepted,
        _ => Unstated,
    };

    /// <summary>
    /// How the outcome reads in a findings report, so every report words it identically.
    /// <paramref name="gateSetAccepted"/> is the platform's own read of this node's gate
    /// acceptance at report time (<c>GateSetAcceptance.Decide</c>), never the session's:
    /// <see cref="Unaccepted"/> is parsed out of the session's own text, and a session can write
    /// that word for reasons that have nothing to do with acceptance (it could not run the suite
    /// and reached for a cautious-sounding word). Only the platform's own acceptance check can
    /// tell a genuine hold from that, so a report agreeing with the session gets the ordinary
    /// wording and one that disagrees says so instead of repeating a claim the platform knows is
    /// false (independent pre-PR review, cycle 1, adversarial finding).
    /// </summary>
    public string Describe(bool gateSetAccepted) =>
        this == Pass ? "ran on the review worktree and passed"
        : this == Fail ? "ran on the review worktree and failed; the evidence is in the report below"
        : this == Absent ? "this project has none to run, so nothing was observed"
        : this == Unaccepted
            ? gateSetAccepted
                ? "the session reported \"unaccepted\", but this project's verify gate set is "
                  + "accepted on this node — that claim is unreliable, and this line does not say "
                  + "whether a suite actually ran; read the report below for what it observed"
                : "not run — this project's verify gate set is unaccepted on this node, so this review "
                  + "withheld its recorded gate commands and ran none of them"
            : "not reported by this session";

    public static implicit operator string(QaEndToEndOutcome? value) => value?.Value ?? string.Empty;

    public bool Equals(QaEndToEndOutcome? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class QaEndToEndOutcomeJsonConverter : JsonConverter<QaEndToEndOutcome>
    {
        public override QaEndToEndOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, QaEndToEndOutcome value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
