using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Whether a pull request GitHub assigns to this install's own login, in this project's repo,
/// automatically mints and starts a pr-review task — and how fast the resulting task dispatches.
/// Off is both the default and the explicit "don't", the <see cref="BacklogPolicy.None"/> idiom:
/// a project that never set this and one told to stop read identically.
/// <para>
/// The three non-off speeds are the general dispatch levers this feature deliberately builds no
/// scheduling code of its own on top of (idea e5e98a33, blocked behind task 45136b29's queue-first
/// marker and shared ceiling-exempt start): Normal joins the ordinary claim rotation exactly as an
/// assigned task always has; First also marks the task queue-first
/// (<see cref="Tasks.Events.TaskRevised.QueuePriority"/>, Decisions Log #127), so it takes the
/// next free dispatch slot regardless of assignment age; Now claims it immediately, ceiling-exempt,
/// through the same sentinel-node-id mechanism <c>h9k task start</c>/<c>h9k task handback --now</c>
/// already use (Decisions Log #103, #125). A human can re-speed any auto-created task afterward
/// with the identical general levers (<c>h9k task revise --queue-first</c>, <c>h9k task start</c>),
/// since nothing about the created task is special.
/// </para>
/// </summary>
[JsonConverter(typeof(AutoPrReviewSpeedJsonConverter))]
public sealed record AutoPrReviewSpeed
{
    /// <summary>The platform's original behavior: no automatic pr-review task is ever minted.</summary>
    public static readonly AutoPrReviewSpeed Off = new("Off");

    /// <summary>Mint, publish, and assign — the task joins the ordinary queue like any other.</summary>
    public static readonly AutoPrReviewSpeed Normal = new("Normal");

    /// <summary>Normal, plus the general queue-first marker: next free slot regardless of age.</summary>
    public static readonly AutoPrReviewSpeed First = new("First");

    /// <summary>Normal, then claimed immediately, ceiling-exempt, through the shared start-it-mine mechanism.</summary>
    public static readonly AutoPrReviewSpeed Now = new("Now");

    public string Value { get; }

    private AutoPrReviewSpeed(string value) => Value = value;

    public static implicit operator string(AutoPrReviewSpeed? speed) => speed?.Value ?? Off.Value;

    /// <summary>
    /// Raw wrapping, not validation — the BacklogPolicy/CommitStyle convention: a value built this
    /// way can carry anything, which is what lets <see cref="Handlers.ProjectDecider.ChangeSettings"/>
    /// be the one place that actually enforces the closed set.
    /// </summary>
    public static implicit operator AutoPrReviewSpeed(string? value) =>
        value.IsBlank() ? Off : new AutoPrReviewSpeed(value);

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as Off.</summary>
    public static AutoPrReviewSpeed FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "normal" => Normal,
        "first" => First,
        "now" => Now,
        _ => Off,
    };

    /// <summary>The strict form a human's own input goes through: a typo is refused, never silently read as off.</summary>
    public static AutoPrReviewSpeed Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? Off
            : FromInput(trimmed) is { } parsed && parsed != Off
                ? parsed
                : throw new DomainValidationException(
                    $"'{RelayedSpeed(trimmed)}' is not an auto-pr-review speed. Use off, normal, first, or now.");
    }

    /// <summary>
    /// What a refused speed is safe to be quoted as — the <see cref="BacklogPolicy"/> convention:
    /// this value comes off a command line and the refusal is printed to a terminal, so a control
    /// character or a bidirectional override in it cannot reach the refusal explaining it, and an
    /// unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string RelayedSpeed(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '?';

    public bool Equals(AutoPrReviewSpeed? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class AutoPrReviewSpeedJsonConverter : JsonConverter<AutoPrReviewSpeed>
    {
        public override AutoPrReviewSpeed Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, AutoPrReviewSpeed value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
