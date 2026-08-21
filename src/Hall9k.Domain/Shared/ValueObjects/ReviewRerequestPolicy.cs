using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Whether the closeout monitor asks the pull request's reviewers to look again after a
/// fix follow-up pushed (Decisions Log #62). Enabled means the reviewer countersigns that
/// its own findings were addressed; Disabled means the pull request settles on the
/// internal review, the thread replies, and CI — the guards that already ran before the
/// fixes were pushed. Unknown means "no opinion at this level", which is how every
/// optional link in the resolution chain says "ask the next one down".
/// <para>
/// Default off, deliberately. Each re-request spends review quota and invites the loop
/// this whole feature has to be bounded against: a reviewer re-reads a diff it has
/// already read, finds a new sample of nits, and the fixes for those nits earn another
/// re-request. The option exists because a countersignature is genuinely worth buying on
/// some work; it is off by default so the price is always chosen rather than inherited.
/// </para>
/// </summary>
[JsonConverter(typeof(ReviewRerequestPolicyJsonConverter))]
public sealed record ReviewRerequestPolicy
{
    /// <summary>Ask the reviewers for another pass once a fix follow-up has pushed.</summary>
    public static readonly ReviewRerequestPolicy Enabled = new("Enabled");

    /// <summary>Never ask; the pull request settles on the internal review, the replies, and CI.</summary>
    public static readonly ReviewRerequestPolicy Disabled = new("Disabled");

    /// <summary>Not recognized or not set at this level. Serializes as an empty string.</summary>
    public static readonly ReviewRerequestPolicy Unknown = new("");

    public string Value { get; }

    private ReviewRerequestPolicy(string value) => Value = value;

    public static implicit operator string(ReviewRerequestPolicy? policy) => policy?.Value ?? string.Empty;

    public static implicit operator ReviewRerequestPolicy(string? value) =>
        value.IsBlank() ? Unknown : new ReviewRerequestPolicy(value);

    /// <summary>
    /// Maps user or config input to the closed set, case-insensitively. <c>on</c>/<c>off</c>
    /// are accepted alongside the canonical names because that is how the CLI option reads,
    /// and <c>default</c> is Unknown — the one idiom that clears an override so the levels
    /// around it decide (the <see cref="AgentModel"/> convention).
    /// </summary>
    public static ReviewRerequestPolicy FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "enabled" or "on" or "true" or "yes" => Enabled,
        "disabled" or "off" or "false" or "no" => Disabled,
        _ => Unknown,
    };

    /// <summary>
    /// The effective policy: the project's setting, then the owner's, then the node's
    /// platform default, and off when nothing said otherwise.
    /// <para>
    /// Project over owner is the model-policy shape (Decisions Log #33) applied to a
    /// narrower question: the more specific level wins, and a repository is more specific
    /// than the person who owns it. An owner turning this on says "I want my work
    /// countersigned"; a project turning it off says "not in this repository", and the
    /// repository is where the review culture actually lives. The node default sits under
    /// both because it describes a machine, which is the least specific thing here.
    /// </para>
    /// </summary>
    public static ReviewRerequestPolicy Resolve(
        ReviewRerequestPolicy? projectPolicy, ReviewRerequestPolicy? ownerPolicy, string? platformDefault)
    {
        foreach (ReviewRerequestPolicy candidate in new[]
                 {
                     FromInput(projectPolicy),
                     FromInput(ownerPolicy),
                     FromInput(platformDefault),
                 })
        {
            if (candidate != Unknown)
            {
                return candidate;
            }
        }

        return Disabled;
    }

    public bool Equals(ReviewRerequestPolicy? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class ReviewRerequestPolicyJsonConverter : JsonConverter<ReviewRerequestPolicy>
    {
        public override ReviewRerequestPolicy Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, ReviewRerequestPolicy value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
