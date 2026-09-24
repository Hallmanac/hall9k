namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The reasoning effort level a dispatched Claude Code session runs at: the five names the
/// <c>effortLevel</c> settings key accepts. A closed vocabulary rather than a pass-through string,
/// because the value is written into a generated JSON settings file and an unrecognized word must
/// never reach it. Unknown means "not set", which leaves the key out of the file so the model's own
/// default decides.
/// </summary>
public sealed record AgentEffort
{
    public static readonly AgentEffort Low = new("low");
    public static readonly AgentEffort Medium = new("medium");
    public static readonly AgentEffort High = new("high");
    public static readonly AgentEffort ExtraHigh = new("xhigh");
    public static readonly AgentEffort Max = new("max");

    /// <summary>Not recognized or not set. Serializes as an empty string.</summary>
    public static readonly AgentEffort Unknown = new("");

    /// <summary>The clearing word <c>h9k config set --effort</c> accepts; it is never a level.</summary>
    public const string ClearingWord = "default";

    /// <summary>The five accepted names, in ascending order, for messages that quote them.</summary>
    public static IReadOnlyList<AgentEffort> All { get; } = [Low, Medium, High, ExtraHigh, Max];

    public string Value { get; }

    private AgentEffort(string value) => Value = value;

    public static AgentEffort FromInput(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "low" => Low,
        "medium" => Medium,
        "high" => High,
        "xhigh" => ExtraHigh,
        "max" => Max,
        _ => Unknown,
    };

    /// <summary>The five accepted names as a comma-separated list, for messages that quote them.</summary>
    public static string DescribeAccepted() => string.Join(", ", All.Select(effort => effort.Value));

    public bool IsWellFormed => this != Unknown;

    public override string ToString() => Value;
}
