using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// What has to be true, on this install, before a task linked to an external item may be
/// claimed here (idea 64c75e43). Every teammate runs their own install against their own
/// database, so the Jira card or the GitHub issue is the only record the two machines share —
/// and <see cref="TrackerAssignee"/> makes the tracker's own assignment field the single act
/// that hands out work: a linked task is claimed here only while the tracker shows that item
/// assigned to this install's own tracker identity, so two teammates' installs cannot both run
/// the same card.
/// <para>
/// <see cref="Off"/> is both the default and the explicit "don't", the
/// <see cref="BacklogPolicy.None"/>/<see cref="AutoPrReviewSpeed.Off"/> idiom: a project that
/// never set this and one told to stop gating read identically, and both behave exactly as the
/// platform did before the setting existed.
/// </para>
/// <para>
/// The precedent this is modelled on is <see cref="AutoPrReviewSpeed"/>, which already makes a
/// GitHub assignment the go signal for one item kind (a pull request's reviewer request). This
/// setting applies the same rule at the claim doors for the other item kinds, read-only: the
/// gate never writes to the tracker, and there is no override flag — loopholes exist (a person
/// can assign the card to themselves and walk away) and are accepted, because the point is a
/// single shared go signal rather than an enforcement boundary.
/// </para>
/// </summary>
[JsonConverter(typeof(ClaimGateJsonConverter))]
public sealed record ClaimGate
{
    /// <summary>The platform's original behavior: assignment inside Hall9k is the only claim rule.</summary>
    public static readonly ClaimGate Off = new("Off");

    /// <summary>
    /// A task linked to a Jira card or a GitHub issue is claimed on this install only while the
    /// tracker shows that item assigned to this install's own tracker identity — the Jira
    /// accountId recorded on the registered connection, or the login <c>gh</c> is authenticated
    /// as, both read from the tracker and never typed.
    /// </summary>
    public static readonly ClaimGate TrackerAssignee = new("TrackerAssignee");

    public string Value { get; }

    private ClaimGate(string value) => Value = value;

    public static implicit operator string(ClaimGate? gate) => gate?.Value ?? Off.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="AutoPrReviewSpeed"/> convention: a value
    /// built this way can carry anything, which is what lets
    /// <see cref="Handlers.ProjectDecider.ChangeSettings"/> be the one place that actually
    /// enforces the closed set.
    /// </summary>
    public static implicit operator ClaimGate(string? value) =>
        value.IsBlank() ? Off : new ClaimGate(value);

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as Off.</summary>
    public static ClaimGate FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "tracker-assignee" or "trackerassignee" => TrackerAssignee,
        _ => Off,
    };

    /// <summary>The strict form a human's own input goes through: a typo is refused, never silently read as off.</summary>
    public static ClaimGate Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? Off
            : FromInput(trimmed) is { } parsed && parsed != Off
                ? parsed
                : throw new DomainValidationException(
                    $"'{RelayedGate(trimmed)}' is not a claim gate. Use off or tracker-assignee.");
    }

    /// <summary>
    /// What a refused gate is safe to be quoted as — the <see cref="AutoPrReviewSpeed"/>
    /// convention: this value comes off a command line and the refusal is printed to a terminal,
    /// so a control character or a bidirectional override in it cannot reach the refusal
    /// explaining it, and an unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string RelayedGate(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '?';

    public bool Equals(ClaimGate? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ClaimGateJsonConverter : JsonConverter<ClaimGate>
    {
        public override ClaimGate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, ClaimGate value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
