using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The one disposition a resolve-review-threads triage gives an unresolved thread, whoever
/// opened it (task: every review thread on a pull request gets a triage disposition before any
/// fix work — origin: two full fix laps in two days bought by false Copilot threads on PR #199
/// and PR #229, both resolved by hand on Brian's word after evidence, with no way for the
/// lifecycle to do that itself). Recorded per thread on the run stream so the decline rate is
/// measurable, and read by <c>ReviewResultParser.ParseThreadDispositions</c> off the agent's own
/// closing summary — the same "the machinery reads a marker, never guesses" discipline every
/// other review outcome in this codebase already follows.
/// </summary>
[JsonConverter(typeof(ReviewThreadDispositionJsonConverter))]
public sealed record ReviewThreadDisposition
{
    /// <summary>The finding is real and in scope: the fix session's work is this thread and no other.</summary>
    public static readonly ReviewThreadDisposition Fix = new("Fix");

    /// <summary>
    /// The finding does not hold up — disproved with reproduction-grade evidence (a scratch-repo
    /// demonstration, or a pointer to the code path that already handles it), not merely disagreed
    /// with. The evidence is the reply that lands in the thread; a bot's thread may then be
    /// resolved, a human's stays open for them to close.
    /// </summary>
    public static readonly ReviewThreadDisposition Decline = new("Decline");

    /// <summary>Real, but out of this task's own scope: filed as a card rather than grown into this diff.</summary>
    public static readonly ReviewThreadDisposition Route = new("Route");

    /// <summary>Not recognized, or a triage recorded before this vocabulary existed. Serializes as an empty string.</summary>
    public static readonly ReviewThreadDisposition Unknown = new("");

    public string Value { get; }

    private ReviewThreadDisposition(string value) => Value = value;

    public static ReviewThreadDisposition Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fix" => Fix,
        "decline" => Decline,
        "route" => Route,
        _ => Unknown,
    };

    public static implicit operator string(ReviewThreadDisposition? value) => value?.Value ?? string.Empty;

    public static implicit operator ReviewThreadDisposition(string? value) =>
        value.IsBlank() ? Unknown : new ReviewThreadDisposition(value);

    public bool Equals(ReviewThreadDisposition? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewThreadDispositionJsonConverter : JsonConverter<ReviewThreadDisposition>
    {
        public override ReviewThreadDisposition Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(
            Utf8JsonWriter writer, ReviewThreadDisposition value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
