using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Whether a pre-approved task's merge gate may trust an empty check rollup as "this project has
/// no CI" once the checks-registration settle window elapses (security review idea 6be68ee2,
/// daemon-consumers finding A), as <c>h9k project set --ci</c> records it. An empty rollup is
/// indistinguishable from a repository with no CI configured at all AND from a workflow run that
/// simply has not registered yet
/// (<c>Hall9k.Daemon.Closeout.PullRequestSnapshot.HasObservedChecks</c>'s own doc), so the
/// gate no longer resolves that ambiguity by trusting silence — it parks and names this setting,
/// and only a project that has said out loud it runs no CI is read as "no CI" past the window.
/// <para>
/// <see cref="Required"/> is both the default and the explicit "this project has CI, or I have not
/// said otherwise" — the <see cref="ClaimGate.Off"/> idiom, a closed set of static instances rather
/// than a bare bool so a later value that reads a repository's own required-checks list off GitHub
/// has somewhere to land without a breaking change to every reader of this type.
/// </para>
/// </summary>
[JsonConverter(typeof(CiPolicyJsonConverter))]
public sealed record CiPolicy
{
    /// <summary>
    /// The default: an empty check rollup past the settle window parks the merge rather than
    /// merging past an unobserved CI result.
    /// </summary>
    public static readonly CiPolicy Required = new("Required");

    /// <summary>
    /// An explicit declaration that this project runs no CI at all — set by a human, never
    /// inferred: an empty check rollup past the settle window is trusted and the merge proceeds,
    /// exactly as it did before this setting existed.
    /// </summary>
    public static readonly CiPolicy None = new("None");

    public string Value { get; }

    private CiPolicy(string value) => Value = value;

    public static implicit operator string(CiPolicy? policy) => policy?.Value ?? Required.Value;

    /// <summary>
    /// Raw wrapping, not validation — the <see cref="ClaimGate"/> convention: a value built this
    /// way can carry anything, which is what lets
    /// <see cref="Handlers.ProjectDecider.ChangeSettings"/> be the one place that actually
    /// enforces the closed set.
    /// </summary>
    public static implicit operator CiPolicy(string? value) =>
        value.IsBlank() ? Required : new CiPolicy(value);

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as Required.</summary>
    public static CiPolicy FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => None,
        _ => Required,
    };

    /// <summary>The strict form a human's own input goes through: a typo is refused, never silently read as required.</summary>
    public static CiPolicy Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("required", StringComparison.OrdinalIgnoreCase)
            ? Required
            : trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? None
                : throw new DomainValidationException(
                    $"'{RelayedPolicy(trimmed)}' is not a CI policy. Use none or required.");
    }

    /// <summary>
    /// What a refused policy is safe to be quoted as — the <see cref="ClaimGate"/> convention:
    /// this value comes off a command line and the refusal is printed to a terminal, so a control
    /// character or a bidirectional override in it cannot reach the refusal explaining it, and an
    /// unbounded argument cannot be echoed whole.
    /// </summary>
    private const int MaximumRelayedLength = 40;

    private static string RelayedPolicy(string value)
    {
        string visible = new([.. value.Take(MaximumRelayedLength).Select(Legible)]);
        return value.Length > MaximumRelayedLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '?';

    public bool Equals(CiPolicy? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class CiPolicyJsonConverter : JsonConverter<CiPolicy>
    {
        public override CiPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, CiPolicy value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
