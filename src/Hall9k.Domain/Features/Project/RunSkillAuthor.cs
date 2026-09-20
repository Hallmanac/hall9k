using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Who composed the run skill currently on the ledger (idea b9b09779, piece 4) — the "author" half
/// of the recorded event, kept honest as a closed vocabulary rather than a name: the three ways a
/// run skill can come to exist are genuinely different in how much a reader should trust it, and
/// none of them is a person's name the platform could observe.
/// </summary>
[JsonConverter(typeof(RunSkillAuthorJsonConverter))]
public sealed record RunSkillAuthor
{
    /// <summary>A discovery session read the repository and composed it.</summary>
    public static readonly RunSkillAuthor DiscoverySession = new("discovery-session");

    /// <summary>A human replaced it by hand through <c>h9k project run-skill set --file</c>.</summary>
    public static readonly RunSkillAuthor Hand = new("hand");

    /// <summary>
    /// No agent composed it at all: the daemon's own repository survey found nothing to read and
    /// recorded the none-discoverable skill itself, without dispatching a session. Its own author
    /// rather than <see cref="DiscoverySession"/> because attributing it to a session that never
    /// ran would be exactly the guessed-at provenance an audit trail must not carry.
    /// </summary>
    public static readonly RunSkillAuthor Platform = new("platform");

    /// <summary>Not one of the three — an unparsed or unrecognized value off an old event.</summary>
    public static readonly RunSkillAuthor Unknown = new(string.Empty);

    public static readonly IReadOnlyList<RunSkillAuthor> All = [DiscoverySession, Hand, Platform];

    public string Value { get; }

    private RunSkillAuthor(string value) => Value = value;

    public static implicit operator string(RunSkillAuthor? author) => author?.Value ?? string.Empty;

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as <see cref="Unknown"/>.</summary>
    public static RunSkillAuthor FromInput(string? value) =>
        All.FirstOrDefault(author => author.Value == value?.Trim().ToLowerInvariant()) ?? Unknown;

    public bool Equals(RunSkillAuthor? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RunSkillAuthorJsonConverter : JsonConverter<RunSkillAuthor>
    {
        public override RunSkillAuthor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            FromInput(reader.GetString());

        public override void Write(Utf8JsonWriter writer, RunSkillAuthor value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
