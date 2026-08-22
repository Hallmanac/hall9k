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

    /// <summary>
    /// What the source itself called this state, when that is not the same word as
    /// <see cref="Value"/>. Null for a source whose vocabulary is already Hall9k's.
    /// <para>
    /// It exists because mapping and observing are two different things and a source with its
    /// own workflow needs both. A Jira card in "In Progress" is open by the platform's rule, and
    /// the rule is what the adoption gate must read; but the agent context stamps what was
    /// observed at import, and printing "open" there would quietly replace the board's own word
    /// with the platform's conclusion about it. Keeping both means the gate reads the mapping
    /// and the human reads the observation (AGENTS.md, never guess at unobserved facts —
    /// including never overwriting an observed one with a derived one).
    /// </para>
    /// </summary>
    public string? SourceLabel { get; }

    private WorkItemStatus(string value, string? sourceLabel = null)
    {
        Value = value;
        SourceLabel = sourceLabel;
    }

    /// <summary>
    /// This state, carrying the word the source used for it. Identical or blank labels are
    /// dropped rather than recorded, so "open (open)" cannot happen; the label is folded to one
    /// printable line first, because it is a value someone else's workflow configuration
    /// supplied and it ends up in a terminal and in an agent's prompt.
    /// <para>
    /// The comparison is against the printed name of the mapping rather than its raw
    /// <see cref="Value"/>, so a source whose own word for a state Hall9k could not map happens
    /// to be "unknown" is recorded as that one word too, rather than as "unknown (unknown)".
    /// </para>
    /// </summary>
    public WorkItemStatus As(string? sourceLabel)
    {
        string label = Text.RelayedText.OneLine(sourceLabel ?? string.Empty).Trim();
        return label.IsBlank() || label.Equals(Mapped, StringComparison.OrdinalIgnoreCase)
            ? this
            : new WorkItemStatus(Value, label);
    }

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
            _ => Unmapped(observed),
        };
    }

    /// <summary>
    /// The source's own word for a state, recorded without the platform recognising anything in
    /// it. It exists for the adapter that has already decided nothing here maps, and it keeps the
    /// two halves of that in their own places: the observed word becomes the
    /// <see cref="SourceLabel"/> and the mapping is <see cref="Unknown"/>, so
    /// <see cref="IsOpen"/> is false whatever the word happens to be and the observation is still
    /// what gets quoted back ("Open (unknown)").
    /// <para>
    /// The distinction matters at a boundary whose vocabulary is not Hall9k's. A source that says
    /// "open" because that is its own word for open should go through <see cref="Parse"/>; an
    /// adapter falling through to this one has established that it could not tell, and a card
    /// whose status merely happens to be named "Open" is not the source saying so. Origin
    /// incident (2026-08-22): the second cycle of this branch's pre-PR review found the Jira
    /// adapter's no-category fallback calling <see cref="Parse"/>, so a card with no
    /// <c>statusCategory</c> and the classic default workflow's "Open" was adopted as open, which
    /// is the guess both that adapter and the decisions log say is refused there.
    /// </para>
    /// <para>
    /// Second origin incident, same day and the same guess one layer down: this method first
    /// recorded the observed word as the <em>mapped</em> value, so <c>Unmapped("open")</c> was
    /// equal to <see cref="Open"/> and read as open — the guard undone by nothing more than a
    /// tenant spelling their status in lower case, which is exactly the coincidence of vocabulary
    /// it was added to refuse. A mapping nobody could make has to be representable as no mapping,
    /// not as the observed text standing in for one.
    /// </para>
    /// </summary>
    public static WorkItemStatus Unmapped(string? value) => Unknown.As(value);

    /// <summary>True only when the source positively said open; Unknown is not open.</summary>
    public bool IsOpen => Value == Open.Value;

    public override string ToString() =>
        SourceLabel is { } label ? $"{label} ({Mapped})" : Mapped;

    private string Mapped => Value.IsBlank() ? "unknown" : Value;
}
