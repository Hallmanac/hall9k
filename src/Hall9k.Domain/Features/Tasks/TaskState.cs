using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>The work lifecycle. Execution detail lives on RunState (TASK-MODEL.md §2).
/// NeedsRefinement is reserved for the funnel era and deliberately absent in v0.
/// Failed is a needs-human waypoint, not a terminal state (Decisions Log #27): an unsolved
/// problem is not an ending. Its exits are retry (re-run), resolve (the objective was met
/// despite the run failure), and abandon (walk away).
/// The first three states separate task development from task dispatch (Decisions Log #34):
/// Draft is being developed, Published is ready to assign, and only assignment makes a task
/// dispatchable — Queued when its dependencies are met, Blocked when they are not.</summary>
[JsonConverter(typeof(TaskStateJsonConverter))]
public sealed record TaskState
{
    /// <summary>Being developed: editable, addressable by id, invisible to the dispatcher.</summary>
    public static readonly TaskState Draft = new("Draft");
    /// <summary>Passed the readiness gate: immutable, referenceable, ready to assign — not claimable.</summary>
    public static readonly TaskState Published = new("Published");
    public static readonly TaskState Queued = new("Queued");
    /// <summary>Assigned, but at least one dependency has not reached true closeout yet.</summary>
    public static readonly TaskState Blocked = new("Blocked");
    public static readonly TaskState Claimed = new("Claimed");
    public static readonly TaskState NeedsHuman = new("NeedsHuman");
    /// <summary>
    /// A pr-review task whose review is posted and whose author has not answered it yet (task: a
    /// pr-review task stays open while the pull request's review threads are unresolved). Not
    /// Done, because a review nobody has answered is not a review that is over; not NeedsHuman,
    /// because nothing is being asked of the reviewer while the ball is in the author's court.
    /// The follow-through poll moves it on: to NeedsHuman when the author replies, pushes, or
    /// re-requests the review — the last of those being the one explicit ask of the reviewer in
    /// the set — and to <see cref="Done"/> only when the pull request itself merges or closes
    /// (Decisions Log #178: every review thread being resolved no longer ends the
    /// wait by itself, so a task stays live for a later GitHub mention of the install's own login
    /// to attach to instead of minting a second one). <c>h9k task abandon</c> remains the one
    /// early exit.
    /// <para>
    /// Origin incident (2026-09-08, arx-platform PR #2023, task 2402246b): the pr-review task
    /// went Done the moment the review was posted, so when the author answered all five threads
    /// and pushed revisions the next day, nothing on the board watched the pull request and the
    /// reviewer heard it from GitHub first. There was no state between Done and a fresh
    /// <c>--from-pr</c> adoption, and this is it.
    /// </para>
    /// </summary>
    public static readonly TaskState AwaitingAuthor = new("AwaitingAuthor");
    public static readonly TaskState Done = new("Done");
    public static readonly TaskState Failed = new("Failed");
    public static readonly TaskState Abandoned = new("Abandoned");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly TaskState Unknown = new("");

    public string Value { get; }

    private TaskState(string value) => Value = value;

    /// <summary>Terminal states say how the story ended: Done (the objective was met) or
    /// Abandoned (a human walked away). Failed is deliberately not here — it waits for a
    /// human decision (Decisions Log #27).</summary>
    public bool IsTerminal => this == Done || this == Abandoned;

    /// <summary>
    /// Whether a human's explicit assignment is what put the task here — Queued and Blocked
    /// are the two faces of "assigned" (Decisions Log #34), so unassign accepts exactly these.
    /// </summary>
    public bool IsAssigned => this == Queued || this == Blocked;

    /// <summary>
    /// Whether the task is still being developed rather than dispatched. Draft and Published
    /// are the two development states: neither is claimable, and abandon reaches both.
    /// </summary>
    public bool IsPreDispatch => this == Draft || this == Published;

    public static implicit operator string(TaskState? value) => value?.Value ?? string.Empty;

    public static implicit operator TaskState(string? value) => value.IsBlank() ? Unknown : new TaskState(value);

    public bool Equals(TaskState? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class TaskStateJsonConverter : JsonConverter<TaskState>
    {
        public override TaskState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, TaskState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
