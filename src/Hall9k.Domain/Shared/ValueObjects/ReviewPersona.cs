using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// One of the fixed review personas a team member can declare on their own member record (idea
/// b9b09779, piece 1): the lens they review somebody else's pull request through. A sealed record
/// with static instances and an <see cref="Unknown"/> sentinel per the house type discipline
/// (TASK-MODEL.md §8), never an enum — the value is persisted on the Owner stream and, once the
/// distributed-team chain ships, on the ledger's own member record.
/// <para>
/// The set is fixed and closed on purpose. A persona is not a free-text role: each one maps to a
/// review prompt and its own criteria in the platform's persona registry, so a persona nothing
/// could register a prompt for would be a declaration the platform can never honour. Widening the
/// set is a code change here plus a registry entry, which is exactly the review that decision
/// deserves.
/// </para>
/// <para>
/// Declaring none is the ordinary case and reads as <see cref="Engineer"/>
/// (<see cref="ForReview"/>), so nothing changes for anyone who never declares one.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewPersonaJsonConverter))]
public sealed record ReviewPersona
{
    /// <summary>Code, logic, functionality: today's pull-request review, unchanged.</summary>
    public static readonly ReviewPersona Engineer = new("engineer");

    /// <summary>Compliance and functionality through the lens of blast radius — what changed, and what adjacent behaviour is owed a regression test.</summary>
    public static readonly ReviewPersona Qa = new("qa");

    /// <summary>User experience, conformance to the proposed design, accessibility, and the project's design system.</summary>
    public static readonly ReviewPersona Designer = new("designer");

    /// <summary>
    /// Not one of the three — a word nobody could read, or a persona recorded by a build that knew
    /// a name this one does not. Serializes as the empty string, and is dropped rather than
    /// guessed at wherever a declared list is read back (<see cref="Declared"/>).
    /// </summary>
    public static readonly ReviewPersona Unknown = new("");

    /// <summary>
    /// The whole set, in the fixed order every persona-ordered surface uses: the findings report's
    /// sections, <c>h9k owner show</c>'s line, and <c>h9k task show</c>'s pane. Fixed rather than
    /// declaration order so two members holding the same personas read identically.
    /// </summary>
    public static readonly IReadOnlyList<ReviewPersona> All = [Engineer, Qa, Designer];

    public string Value { get; }

    private ReviewPersona(string value) => Value = value;

    /// <summary>True for one of the three; false for <see cref="Unknown"/>.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>How this persona's own section of a findings report is headed.</summary>
    public string ReportHeading =>
        this == Engineer ? "Engineer review"
        : this == Qa ? "QA review"
        : this == Designer ? "Design review"
        : "Unrecognized persona";

    /// <summary>
    /// The persona a human typed, or a refusal naming the whole set. Blank is refused rather than
    /// read as <see cref="Unknown"/>: at a command line a blank persona is an empty shell variable,
    /// never a request, and the option that declares none is <c>--clear-personas</c>.
    /// </summary>
    public static ReviewPersona Parse(string? value)
    {
        ReviewPersona read = Read(value);
        return read.HasValue
            ? read
            : throw new DomainValidationException(
                $"'{Legible(value)}' is not a review persona. The set is fixed: "
                + $"{string.Join(", ", All.Select(persona => persona.Value))}. Each one maps to its own "
                + "review prompt and criteria in the platform's persona registry, so there is no "
                + "free-text persona to declare.");
    }

    /// <summary>The tolerant read — anything unrecognized is <see cref="Unknown"/>, never a guess.</summary>
    public static ReviewPersona Read(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "engineer" => Engineer,
        "qa" => Qa,
        "designer" => Designer,
        _ => Unknown,
    };

    /// <summary>
    /// What a member actually declared, normalized: every recognized persona once, in
    /// <see cref="All"/>'s fixed order, with <see cref="Unknown"/> dropped. An empty result means
    /// they declared nothing — <see cref="ForReview"/>, not this, is what turns that into the
    /// engineer's review.
    /// </summary>
    public static IReadOnlyList<ReviewPersona> Declared(IEnumerable<ReviewPersona>? personas)
    {
        if (personas is null)
        {
            return [];
        }

        HashSet<string> held =
            [.. personas.Where(persona => persona is { HasValue: true }).Select(persona => persona.Value)];
        return [.. All.Where(persona => held.Contains(persona.Value))];
    }

    /// <summary>
    /// The personas a pull request assigned to this member is actually reviewed through:
    /// <see cref="Declared"/>, or <see cref="Engineer"/> alone when they declared nothing. This is
    /// the "no persona reads as engineer" rule, in one place, so the dispatch, the report, and
    /// every surface that prints what ran all read it the same way.
    /// </summary>
    public static IReadOnlyList<ReviewPersona> ForReview(IEnumerable<ReviewPersona>? declared)
    {
        IReadOnlyList<ReviewPersona> held = Declared(declared);
        return held.Count == 0 ? [Engineer] : held;
    }

    /// <summary>
    /// What a refused word is safe to be quoted as, on the same whitelist terms
    /// <see cref="VoiceSkillName"/> states in full: the value came off a command line and the
    /// refusal is printed to a terminal, so only what a persona could legally have been spelled
    /// with survives.
    /// </summary>
    private static string Legible(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        string visible = new([.. trimmed.Take(32).Select(Readable)]);
        return trimmed.Length > 32 ? visible + "…" : visible;
    }

    private static char Readable(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ' ' ? character : '?';

    // No implicit string conversions, deliberately, unlike the Run feature's own lens and scope
    // value objects: a persona is always either parsed (refusing a word nobody could read) or
    // read tolerantly, and an implicit conversion is exactly the seam through which a typo would
    // become Unknown without anyone asking for that reading. VoiceSkillName, the closest
    // analogue, makes the same call.
    public bool Equals(ReviewPersona? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewPersonaJsonConverter : JsonConverter<ReviewPersona>
    {
        // Reading goes through the tolerant Read, not Parse, on the same terms every other value
        // object's converter here states: a value already on a stream is a record of what was set,
        // and a set narrowed later must not make an old document unreadable.
        public override ReviewPersona Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ReviewPersona.Read(reader.GetString());

        public override void Write(Utf8JsonWriter writer, ReviewPersona value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
