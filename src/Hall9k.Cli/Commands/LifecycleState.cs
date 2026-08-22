namespace Hall9k.Cli.Commands;

/// <summary>
/// The lifecycle word a human reads in the Status column: where the work is, and nothing else
/// (Decisions Log #66). It answers exactly one of the four questions the old single field was
/// carrying — the other three are <see cref="TaskPhase"/> (what the machinery is doing right
/// now), <see cref="TaskAttention"/> (whether it wants a human, and why), and the derived-facts
/// line beside a Published row.
/// <para>
/// It is a display vocabulary, composed from the persisted <see cref="Domain.Features.Tasks.TaskState"/>
/// and the current run's records. Nothing persists it: the streams still record Queued, Blocked
/// and Claimed exactly as they did, and this is what those states are shown as. That is the
/// display-first decision (Brian, 2026-08-22) — Queued and Blocked retire when the ranking model
/// lands (IDEA-ranking-and-grooming), and when they do only <see cref="TaskStatusComposer"/>
/// changes.
/// </para>
/// <para>
/// A sealed record with static instances rather than an enum, because the word and the colour
/// travel together and every surface must spell the state the same way (TASK-MODEL.md §8).
/// </para>
/// </summary>
internal sealed record LifecycleState
{
    /// <summary>Being developed: editable, invisible to the dispatcher.</summary>
    public static readonly LifecycleState Draft = new("Draft", "dim");

    /// <summary>
    /// Past the readiness gate and not yet working: waiting to be assigned, waiting to be
    /// claimed, or waiting on a dependency. Which of those it is goes on the derived-facts line,
    /// never in this column.
    /// </summary>
    public static readonly LifecycleState Published = new("Published", "blue");

    /// <summary>A run owns the task: building, gating, or in the pre-PR review loop.</summary>
    public static readonly LifecycleState Working = new("Working", "yellow");

    /// <summary>
    /// The work is pushed and the merge has not been observed — the state the old premature
    /// Done was falsely claiming. Everything from the pull request opening to the closeout
    /// monitor seeing it merged lives here, follow-up runs included.
    /// </summary>
    public static readonly LifecycleState Delivered = new("Delivered", "magenta");

    /// <summary>
    /// True closeout: the merge was observed, or the task was closed with no pull request to
    /// watch. This is the same bar the dependency rule uses (TASK-MODEL.md §2.3), which is the
    /// point — the board and the blocker rule finally agree on the word.
    /// </summary>
    public static readonly LifecycleState Done = new("Done", "green");

    /// <summary>A waypoint, not an ending (Decisions Log #27): it waits for retry, resolve, or abandon.</summary>
    public static readonly LifecycleState Failed = new("Failed", "red");

    /// <summary>A human walked away. Whichever word the stream records for it is shown as this one.</summary>
    public static readonly LifecycleState Archived = new("Archived", "dim");

    /// <summary>A state this build does not recognize. Named rather than blanked, so a gap reads as one.</summary>
    public static readonly LifecycleState Unknown = new("Unknown", "dim");

    /// <summary>The whole vocabulary, in lifecycle order — what --state offers and --help lists.</summary>
    public static readonly IReadOnlyList<LifecycleState> All =
        [Draft, Published, Working, Delivered, Done, Failed, Archived];

    /// <summary>The word itself, as every surface prints it.</summary>
    public string Word { get; }

    /// <summary>Its Spectre colour, so the Status column reads the same on every surface.</summary>
    public string Colour { get; }

    private LifecycleState(string word, string colour)
    {
        Word = word;
        Colour = colour;
    }

    public string Markup => $"[{Colour}]{Word}[/]";

    public bool Equals(LifecycleState? other) => other is not null && Word == other.Word;

    public override int GetHashCode() => Word.GetHashCode();

    public override string ToString() => Word;
}
