using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The reasoning effort level a dispatched agent session runs at: the four names the one executor today
/// accepts in a persisted settings file (never <c>max</c>, which that executor narrows away and silently
/// drops from a file, so it is session-only there). A closed vocabulary rather than a pass-through string,
/// because the value is written into a generated JSON settings file and an unrecognized word must
/// never reach it. Unknown means "not set", which leaves the key out of the file so the model's own
/// default decides.
/// <para>
/// The names are vendor-neutral on purpose: the executor's own settings key is known only to that executor
/// and to the settings-file writer, so a second executor adds one mapping from these four values and
/// touches no resolution code. <see cref="Resolve"/> is the whole resolution chain.
/// </para>
/// </summary>
[JsonConverter(typeof(AgentEffortJsonConverter))]
public sealed record AgentEffort
{
    public static readonly AgentEffort Low = new("low");
    public static readonly AgentEffort Medium = new("medium");
    public static readonly AgentEffort High = new("high");
    public static readonly AgentEffort ExtraHigh = new("xhigh");

    /// <summary>Not recognized or not set.</summary>
    public static readonly AgentEffort Unknown = new("");

    /// <summary>The clearing word <c>h9k config set --effort</c> accepts; it is never a level.</summary>
    public const string ClearingWord = "default";

    /// <summary>The four accepted names, in ascending order, for messages that quote them.</summary>
    public static IReadOnlyList<AgentEffort> All { get; } = [Low, Medium, High, ExtraHigh];

    public string Value { get; }

    private AgentEffort(string value) => Value = value;

    public static AgentEffort FromInput(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "low" => Low,
        "medium" => Medium,
        "high" => High,
        "xhigh" => ExtraHigh,
        _ => Unknown,
    };

    /// <summary>The four accepted names as a comma-separated list, for messages that quote them.</summary>
    public static string DescribeAccepted() => string.Join(", ", All.Select(effort => effort.Value));

    /// <summary>
    /// The effective level for a session: the task's own value, then the project's, then the node's value
    /// for the session's role, then the node-wide value, and Unknown when nothing sets one, which leaves
    /// the executor's settings key out so the model's own default decides. The further down the chain,
    /// the higher the priority, and every link is optional.
    /// </summary>
    public static AgentEffort Resolve(
        AgentEffort? taskOverride, AgentEffort? projectOverride, AgentEffort? roleDefault, AgentEffort? nodeDefault) =>
        FirstSet(taskOverride, projectOverride, roleDefault, nodeDefault);

    /// <summary>
    /// The first candidate that names a level, or Unknown. Used by <see cref="Resolve"/>, and by a caller
    /// whose role value is itself a chain (a verify or final review pass falls through to review's value
    /// before anything beneath the role).
    /// </summary>
    public static AgentEffort FirstSet(params ReadOnlySpan<AgentEffort?> candidates)
    {
        foreach (AgentEffort? candidate in candidates)
        {
            if (candidate is { IsWellFormed: true })
            {
                return candidate;
            }
        }

        return Unknown;
    }

    public bool IsWellFormed => this != Unknown;

    public override string ToString() => Value;

    /// <summary>
    /// Reads through <see cref="FromInput"/>, so a hand-edited or older payload lands on Unknown rather
    /// than carrying a word the settings file must never receive.
    /// </summary>
    private sealed class AgentEffortJsonConverter : JsonConverter<AgentEffort>
    {
        public override AgentEffort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            FromInput(reader.GetString());

        public override void Write(Utf8JsonWriter writer, AgentEffort value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
