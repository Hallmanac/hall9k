using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Reads --state against the three surfaces a row now carries (Decisions Log #66):
/// <list type="number">
/// <item>a <b>lifecycle state</b> (Draft, Published, Working, Delivered, Done, Failed, Archived)
/// selects exactly what the Status column shows;</item>
/// <item>an <b>attention group</b> (needs-you, stalled, attention-working, …) selects the whole
/// group h9k status and the rollups count by;</item>
/// <item>a <b>run state</b> (running, under-review, checks-failing, …) selects on the phase
/// line's material — the vocabulary that used to leak into the Status column and now lives
/// under it.</item>
/// </list>
/// Separators and case are noise: needs-you, needsyou, and NEEDS_YOU all land.
/// <para>
/// <b>No word belongs to two vocabularies, and where one did, the column wins</b> (Brian's
/// ruling, 2026-08-22): the word a reader types selects the rows whose Status column shows that
/// word, because that is the word they just read off the screen. Five group spellings collided
/// with the lifecycle vocabulary the redesign introduced (Draft, Published, Working, Delivered,
/// Done), and each returned a set the column contradicted — <c>--state delivered</c> answered
/// with the rows counted under the Delivered group and silently left out the pushed row parked
/// on a question, which the Status column calls Delivered just the same. The vocabulary that
/// loses a word gets a spelling of its own rather than an unreachable entry in --help: the four
/// groups the column shadowed are spelled <c>attention-working</c>, <c>attention-delivered</c>,
/// <c>attention-draft</c> and <c>attention-done</c>, and the fifth, Ready, has its own name
/// already and no longer answers to <c>published</c>. Origin incident (2026-08-20): the
/// pull-request group was first spelled awaiting-review, which normalized onto the AwaitingReview
/// run state, so <c>--state AwaitingReview</c> silently returned the ChecksFailing and
/// ReviewPending rows too and the state advertised in --help could not be selected at all.
/// <see cref="RunFailedSpelling"/> is that answer applied to the run vocabulary, and the
/// <c>attention-</c> spellings are it applied to the groups.
/// </para>
/// </summary>
internal static class TaskStateFilter
{
    /// <summary>
    /// The groups h9k status and the rollups count by, keyed by their normalized form. A group
    /// whose name the Status column prints too is spelled with an <c>attention-</c> prefix, so
    /// the bare word stays the column's.
    /// </summary>
    private static readonly Dictionary<string, AttentionBucket> AttentionWords = new()
    {
        ["needsyou"] = AttentionBucket.NeedsYou,
        ["stalled"] = AttentionBucket.Stalled,
        ["attentionworking"] = AttentionBucket.Working,
        // What the group was called before the lifecycle vocabulary landed. Kept because a
        // reader's fingers and an agent's saved command line both outlive a rename, and kept
        // bare because neither word is one the Status column prints.
        ["active"] = AttentionBucket.Working,
        ["attentiondelivered"] = AttentionBucket.Delivered,
        ["inreview"] = AttentionBucket.Delivered,
        ["queued"] = AttentionBucket.Queued,
        ["blocked"] = AttentionBucket.Blocked,
        // The group the Status column calls Published, along with Queued and Blocked. It keeps
        // the bare word "ready", which the column never prints; "published" used to reach it too
        // and is the column's now, which selects the wider set rather than a different one.
        ["ready"] = AttentionBucket.Ready,
        ["attentiondraft"] = AttentionBucket.Draft,
        ["attentiondone"] = AttentionBucket.Done,
        ["closed"] = AttentionBucket.Closed,
    };

    /// <summary>
    /// The attention groups as --state spells them, in the order h9k status reads them. A
    /// <c>const</c> rather than an array so the --state <c>[Description]</c> can concatenate
    /// it: the --help tree is a first-class interface (AGENTS.md), and a second hand-typed
    /// copy of this vocabulary is exactly how blocked, ready and draft shipped undiscoverable
    /// while the validation error listed them. Origin incident (2026-08-20): the lifecycle
    /// split added three groups to this filter and the help text kept the pre-split seven.
    /// </summary>
    internal const string AttentionSpelling =
        "needs-you, stalled, attention-working, attention-delivered, queued, blocked, ready, "
        + "attention-draft, attention-done, closed";

    /// <summary>
    /// The lifecycle vocabulary the Status column prints (Decisions Log #66). Every word here is
    /// this vocabulary's alone: what it reaches is exactly the rows showing it in that column,
    /// whatever group they are counted under and whatever their run is doing.
    /// </summary>
    internal static readonly string[] LifecycleStates = [.. LifecycleState.All.Select(state => state.Word)];

    /// <summary>
    /// Words a reader may still type for a state the column has renamed. The stream records
    /// Abandoned and the Status column says Archived (backlog 33 renames the persisted word in
    /// its own pass), so both spellings select the same rows whichever way round that lands.
    /// </summary>
    private static readonly Dictionary<string, string> LifecycleAliases = new()
    {
        ["abandoned"] = "archived",
    };

    /// <summary>
    /// How --state spells <see cref="RunState.Failed"/>. Failed names a lifecycle state too, and
    /// the two mean opposite things: a task that failed versus a pull request closed without
    /// merging, which leaves the task Delivered. The bare word is the column's, so the run state
    /// gets a spelling of its own rather than a --help entry that quietly returns the other set.
    /// </summary>
    private const string RunFailedSpelling = "run-failed";

    /// <summary>
    /// The run vocabulary, which is the phase line's material. Selectable because "show me
    /// everything under review" is a real question, and unprinted in the Status column because
    /// answering it there is what made the old board unreadable.
    /// <para>
    /// <c>BudgetParked</c> belongs here rather than among the attention groups (Decisions Log
    /// #40): it is a run state like the two parks beside it, so "show me everything waiting on
    /// the budget window" is answered by the same family that answers "show me everything under
    /// review", and no new group is added to a board whose groups are the lifecycle's.
    /// </para>
    /// </summary>
    internal static readonly string[] RunStates =
    [
        RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value,
        RunState.UnderReview.Value, RunState.ReviewParked.Value, RunState.BudgetParked.Value,
        RunState.AwaitingReview.Value, RunState.ChecksFailing.Value, RunState.ReviewPending.Value,
        RunState.CloseoutParked.Value, RunState.Completed.Value, RunFailedSpelling,
        RunState.Killed.Value, RunState.Superseded.Value,
    ];

    /// <summary>
    /// Validates the input up front, so a typo fails with the vocabulary quoted instead of
    /// quietly returning zero rows and reading as "you have no such work".
    /// </summary>
    public static void Validate(string state)
    {
        string normalized = Lifecycle(Normalize(state));
        if (Names(LifecycleStates, normalized) || AttentionWords.ContainsKey(normalized) || Names(RunStates, normalized))
        {
            return;
        }

        throw new DomainValidationException(
            $"--state '{state}' is not a state h9k tracks. Lifecycle states, which are what the Status "
            + $"column shows: {string.Join(", ", LifecycleStates)}. Attention groups (each selects a whole "
            + $"group h9k status counts by): {AttentionSpelling}. Run states, which show on the phase line "
            + $"rather than the Status column: {string.Join(", ", RunStates)}. "
            + "Hyphens are optional and case does not matter.");
    }

    /// <summary>
    /// Whether the row is one the given word asks for. The vocabularies are disjoint by
    /// construction, so the order below is which surface the word belongs to rather than a
    /// precedence anything falls through: the lifecycle word is tried first because it is the
    /// one a reader is holding when they type what the Status column just told them.
    /// </summary>
    public static bool Matches(TaskStatusRow row, string state)
    {
        string normalized = Lifecycle(Normalize(state));
        if (Names(LifecycleStates, normalized))
        {
            return Normalize(row.State.Word) == normalized;
        }

        return AttentionWords.TryGetValue(normalized, out AttentionBucket group)
            ? row.Group == group
            : Names(RunStates, normalized) && Normalize(row.RunState.Value) == RunStateWord(normalized);
    }

    /// <summary>
    /// The run state a run-vocabulary word selects. Every spelling is the state's own word except
    /// <see cref="RunFailedSpelling"/>, which exists because the state's own word is taken.
    /// </summary>
    private static string RunStateWord(string normalized) =>
        normalized == Normalize(RunFailedSpelling)
            ? Normalize(RunState.Failed.Value)
            : normalized;

    private static string Lifecycle(string normalized) =>
        LifecycleAliases.GetValueOrDefault(normalized, normalized);

    private static bool Names(IEnumerable<string> vocabulary, string normalized) =>
        vocabulary.Any(word => Normalize(word) == normalized);

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
