using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Reads --state against the composed display bucket, so a filter selects exactly what the
/// Status column shows. Two vocabularies are accepted: an exact bucket (Running, ChecksFailing, …)
/// selects just that one, an attention word (needs-you, active, …) selects a whole group.
/// Separators and case are noise: needs-you, needsyou, and NEEDS_YOU all land.
/// The two vocabularies must stay distinct, because normalization erases the spelling that
/// tells them apart: the pull-request group is spelled <c>in-review</c> rather than
/// awaiting-review precisely so it cannot shadow the AwaitingReview bucket inside it, and an
/// exact bucket wins any collision that survives. Origin incident (2026-08-20): the group was
/// first spelled awaiting-review, which normalized onto the AwaitingReview bucket, so
/// <c>--state AwaitingReview</c> silently returned the ChecksFailing and ReviewPending rows
/// too and the quiet-PR state advertised in --help could not be selected at all.
/// </summary>
internal static class TaskStateFilter
{
    /// <summary>The groups h9k status and the rollups count by, keyed by their normalized form.</summary>
    private static readonly Dictionary<string, AttentionBucket> AttentionWords = new()
    {
        ["needsyou"] = AttentionBucket.NeedsYou,
        ["stalled"] = AttentionBucket.Stalled,
        ["active"] = AttentionBucket.Active,
        ["inreview"] = AttentionBucket.InReview,
        ["queued"] = AttentionBucket.Queued,
        ["done"] = AttentionBucket.Done,
        ["closed"] = AttentionBucket.Closed,
    };

    internal static readonly string[] AttentionSpelling =
        ["needs-you", "stalled", "active", "in-review", "queued", "done", "closed"];

    /// <summary>
    /// Every bucket TaskStatusComposer.Compose can produce: a task's own work state, the
    /// current run's execution state while claimed, and the synthetic ClosingOut for a
    /// follow-up run in flight (log #18/#22).
    /// </summary>
    internal static readonly string[] Buckets =
    [
        TaskState.Queued.Value, TaskState.Claimed.Value, TaskState.NeedsHuman.Value,
        TaskState.Done.Value, TaskState.Failed.Value, TaskState.Abandoned.Value,
        RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value,
        RunState.UnderReview.Value, RunState.AwaitingReview.Value, RunState.ChecksFailing.Value,
        RunState.ReviewPending.Value, RunState.Completed.Value, RunState.Killed.Value,
        RunState.Superseded.Value, "ClosingOut",
    ];

    /// <summary>
    /// Validates the input up front, so a typo fails with the vocabulary quoted instead of
    /// quietly returning zero rows and reading as "you have no such work".
    /// </summary>
    public static void Validate(string state)
    {
        string normalized = Normalize(state);
        if (IsBucket(normalized) || AttentionWords.ContainsKey(normalized))
        {
            return;
        }

        throw new DomainValidationException(
            $"--state '{state}' is not a state h9k tracks. Attention groups (each selects a whole group): "
            + $"{string.Join(", ", AttentionSpelling)}. Exact states: {string.Join(", ", Buckets)}. "
            + "Hyphens are optional and case does not matter.");
    }

    public static bool Matches(TaskStatusRow row, string state)
    {
        // The exact bucket is read first: a word that names a state means that state, never a
        // wider group that happens to contain it. --help promises exactly this.
        string normalized = Normalize(state);
        return IsBucket(normalized)
            ? Normalize(row.Bucket) == normalized
            : AttentionWords.TryGetValue(normalized, out AttentionBucket attention) && row.Attention == attention;
    }

    private static bool IsBucket(string normalized) =>
        Buckets.Any(bucket => Normalize(bucket) == normalized);

    private static string Normalize(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
