using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Where a published task's work becomes visible outside Hall9k. None is both the default and
/// the explicit "don't" — a project that has never set this and one that was told to stop read
/// identically, the same idiom <see cref="CommitStyle.Unknown"/> and <see cref="ReviewRerequestPolicy.Unknown"/>
/// use for "no override here".
/// <para>
/// GitHubIssues has the platform author the issue itself: an issue's shape (title, body, labels)
/// is uniform enough across repositories that a deterministic render is honest, unlike a Jira
/// card's issue type and required fields, which are one organisation's configuration. Jira
/// dispatches the same agent-mediated push <c>h9k task push-to-jira</c> already used by hand —
/// this policy only automates when that push happens, not how it works (backlog 18).
/// </para>
/// </summary>
[JsonConverter(typeof(BacklogPolicyJsonConverter))]
public sealed record BacklogPolicy
{
    /// <summary>No automatic tracking — the platform's behavior before this feature existed.</summary>
    public static readonly BacklogPolicy None = new("None");

    /// <summary>Publishing authors a GitHub issue and links it, verified read-back included.</summary>
    public static readonly BacklogPolicy GitHubIssues = new("GitHubIssues");

    /// <summary>Publishing requests the agent-mediated Jira push, exactly as a manual push-to-jira would.</summary>
    public static readonly BacklogPolicy Jira = new("Jira");

    public string Value { get; }

    private BacklogPolicy(string value) => Value = value;

    public static implicit operator string(BacklogPolicy? policy) => policy?.Value ?? None.Value;

    /// <summary>
    /// Raw wrapping, not validation — the CommitStyle/ReviewRerequestPolicy convention: a value
    /// built this way can carry anything, which is what lets <see cref="Handlers.ProjectDecider.ChangeSettings"/>
    /// be the one place that actually enforces the closed set. Use <see cref="Parse"/> for a
    /// human's own input and <see cref="FromInput"/> for a value already vetted at another level.
    /// </summary>
    public static implicit operator BacklogPolicy(string? value) => value.IsBlank() ? None : new BacklogPolicy(value);

    /// <summary>Lenient mapping for a value already on the stream or read off another level; unrecognized reads as None.</summary>
    public static BacklogPolicy FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "githubissues" or "github-issues" or "github" => GitHubIssues,
        "jira" => Jira,
        _ => None,
    };

    /// <summary>
    /// The strict form a human's own input goes through (the <see cref="Shared.ValueObjects.JiraProjectKey"/>
    /// convention): a typo here silently sends a project's every future publish nowhere, or to the
    /// wrong backlog, so it is refused at the command line rather than discovered at the next
    /// publish.
    /// </summary>
    public static BacklogPolicy Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.IsBlank() || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? None
            : FromInput(trimmed) is { } parsed && parsed != None
                ? parsed
                : throw new DomainValidationException(
                    $"'{value}' is not a backlog policy. Use none, github-issues, or jira.");
    }

    public bool Equals(BacklogPolicy? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class BacklogPolicyJsonConverter : JsonConverter<BacklogPolicy>
    {
        // Reading is deliberately not FromInput, the JiraProjectKey convention: a value already
        // on an event stream is a record of what was set, and a rule tightened later must not
        // make an old document unreadable.
        public override BacklogPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, BacklogPolicy value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
