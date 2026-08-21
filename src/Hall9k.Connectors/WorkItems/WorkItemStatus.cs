namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Whether the external system said the work item was open or closed, as read at import.
/// A closed-vocabulary value object per the house discipline (TASK-MODEL.md §8), minus the
/// JSON converter its persisted siblings carry: this one is never written to an event. It is
/// an observation the import prints into the task's agent context and then stops asserting,
/// because Hall9k takes a one-time snapshot and never syncs the item afterwards.
/// <para>
/// An unrecognised state rides through as itself rather than collapsing to Unknown: "draft"
/// or "merged" from some future source is a real answer we simply have no rule for, and
/// recording it verbatim is the never-guess rule (AGENTS.md). It rides through as refused,
/// though: the adoption gate asks <see cref="IsOpen"/>, so a provider whose system has its own
/// vocabulary ("in progress", "in review") maps it onto <see cref="Open"/> and
/// <see cref="Closed"/> at its own boundary, where the knowledge to do that honestly lives.
/// A value that survives to here unmapped means nobody could say, and the raw text is then
/// what the refusal quotes back.
/// </para>
/// </summary>
public sealed record WorkItemStatus
{
    public static readonly WorkItemStatus Open = new("open");
    public static readonly WorkItemStatus Closed = new("closed");

    /// <summary>The source said nothing about state. Renders as "unknown", never as "open".</summary>
    public static readonly WorkItemStatus Unknown = new("");

    public string Value { get; }

    private WorkItemStatus(string value) => Value = value;

    /// <summary>
    /// Case and surrounding space are normalised only far enough to recognise the two states
    /// Hall9k has a rule for; anything else keeps the text the source actually sent. A source
    /// that says "In Review" is quoted back as "In Review", because the refusal that quotes it
    /// is an audit line about what was observed, and lower-casing it would already be a small
    /// edit to the record (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    public static WorkItemStatus Parse(string? value)
    {
        string observed = value?.Trim() ?? string.Empty;
        return observed.ToLowerInvariant() switch
        {
            "" => Unknown,
            "open" => Open,
            "closed" => Closed,
            _ => new WorkItemStatus(observed),
        };
    }

    /// <summary>True only when the source positively said open; Unknown is not open.</summary>
    public bool IsOpen => Value == Open.Value;

    public override string ToString() => Value.IsBlank() ? "unknown" : Value;
}
