using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// What one look at a stacked child's <em>remote</em> parent found — the pull request another
/// install owns that this task declared itself stacked on (task: a stacked child can stand on a
/// pull request another install owns). A closed vocabulary that lands on an event
/// (<see cref="Events.RemoteStackedParentObserved"/>), so a sealed record with static instances
/// rather than an enum (TASK-MODEL.md §8) — unlike
/// <c>Hall9k.Daemon.Closeout.StackedParentVerdict</c>, which is an in-process outcome nothing
/// persists.
/// <para>
/// The vocabulary is deliberately the pull request's own, not the local lifecycle's: a reviewer's
/// node never holds the parent's run, so <c>Delivered</c>, <c>Done</c> and the rest have no
/// meaning here. <see cref="Open"/> <em>is</em> the remote parent's Delivered (Brian's ruling,
/// 2026-09-07: the pull request being open is what a remote parent's Delivered means, and slice
/// two's earlier start applies only to local parents).
/// </para>
/// </summary>
[JsonConverter(typeof(RemoteParentStateJsonConverter))]
public sealed record RemoteParentState
{
    /// <summary>
    /// The pull request exists and is open. The remote parent's Delivered: there is a head branch
    /// on origin to cut from and a pull request to target, which is everything a stacked child
    /// needs from its parent.
    /// </summary>
    public static readonly RemoteParentState Open = new("Open");

    /// <summary>
    /// The pull request merged. The stack edge's usefulness ends here exactly as a local parent's
    /// closeout ends it: the head branch is going away, so a child that has not dispatched yet is
    /// an ordinary task on the project's own base, and one already delivered owes the retarget and
    /// the replay.
    /// </summary>
    public static readonly RemoteParentState Merged = new("Merged");

    /// <summary>
    /// The pull request closed without merging. A base nothing further arrives on — slice two's
    /// dead-parent rule, one pull request over, so the child parks for a human rather than
    /// building on it.
    /// </summary>
    public static readonly RemoteParentState ClosedUnmerged = new("ClosedUnmerged");

    /// <summary>
    /// The provider answered, and there is no pull request with that number in this repository.
    /// Deliberately distinct from <see cref="Unknown"/>, which is a read that FAILED: nothing here
    /// failed. And deliberately not read as dead either — the ordinary shape is a number a human
    /// declared before the teammate opened the pull request, which resolves itself the moment they
    /// do, so an undispatched child waits rather than being handed to a human.
    /// </summary>
    public static readonly RemoteParentState Absent = new("Absent");

    /// <summary>
    /// Nothing was observed: never looked at yet, or a look that could not be made. Never treated
    /// as any of the others — a failed <c>gh</c> call is not evidence about a pull request
    /// (AGENTS.md's never-guess rule). Serializes as an empty string.
    /// </summary>
    public static readonly RemoteParentState Unknown = new("");

    public string Value { get; }

    private RemoteParentState(string value) => Value = value;

    /// <summary>
    /// Whether a child stacked on a parent in this state may dispatch. Open is the parent's
    /// Delivered; Merged frees the child too, because there is nothing left to stack on and the
    /// project's own base is where the work belongs — the identical reading
    /// <c>StackedBaseResolver</c> gives a local parent that has closed out.
    /// </summary>
    public bool ReleasesChild => this == Open || this == Merged;

    /// <summary>
    /// Whether this state was actually observed, as opposed to <see cref="Unknown"/>'s "nothing
    /// was seen". What a caller checks before recording an observation or acting on one.
    /// </summary>
    public bool WasObserved => this != Unknown;

    /// <summary>
    /// What a waiting child is waiting for, as a clause after "waiting for" — the sentence
    /// <c>h9k task show</c> and the phase line both put in front of a human. Only meaningful for a
    /// state that does not release the child; the two that do answer with what already happened
    /// instead, so a caller that asks anyway gets a true sentence rather than a blank.
    /// <para>
    /// <see cref="Absent"/> reads as absent rather than as "not opened yet", deliberately: the
    /// provider answers identically for a pull request the teammate has not opened, a mistyped
    /// number, and an ISSUE number passed by mistake (verified against <c>gh</c>, 2026-09-07 — all
    /// three come back "Could not resolve to a PullRequest"). Which of the three it is was never
    /// observed, so the word says what was (AGENTS.md's never-guess rule); the doc on
    /// <see cref="Absent"/> is where the ordinary case is named, because that is a note to a
    /// reader of this code rather than a claim on a human's screen.
    /// </para>
    /// </summary>
    public string Describe() =>
        this == Open ? "open"
        : this == Merged ? "merged"
        : this == ClosedUnmerged ? "closed without merging"
        : this == Absent ? "absent from this repository"
        : "not observed yet";

    public static implicit operator string(RemoteParentState? value) => value?.Value ?? string.Empty;

    public static implicit operator RemoteParentState(string? value) => value.IsBlank()
        ? Unknown
        : value == Open.Value ? Open
        : value == Merged.Value ? Merged
        : value == ClosedUnmerged.Value ? ClosedUnmerged
        : value == Absent.Value ? Absent
        : Unknown;

    public bool Equals(RemoteParentState? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class RemoteParentStateJsonConverter : JsonConverter<RemoteParentState>
    {
        public override RemoteParentState Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, RemoteParentState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
