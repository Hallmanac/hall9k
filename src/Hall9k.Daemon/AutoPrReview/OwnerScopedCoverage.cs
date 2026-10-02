using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Marten;

namespace Hall9k.Daemon.AutoPrReview;

/// <summary>A task covering a pull request, with the ownership facts <see cref="TaskOwnerRule"/> judges it by.</summary>
internal sealed record CoveringTaskCandidate(TaskListItem Task, TaskOwnerFacts Facts);

/// <summary>An unpersisted, in-process answer, so an enum rather than a closed vocabulary.</summary>
internal enum ReviewCoverageKind
{
    /// <summary>No live task covers the pull request: the trigger decides a mint, or a hold.</summary>
    Uncovered,

    /// <summary>A live task of this owner's covers it: nothing is minted, and a mention attaches to it.</summary>
    Own,

    /// <summary>Only another owner's live task covers it: that task is theirs, so nothing here may touch it.</summary>
    Teammate,

    /// <summary>
    /// No live task of this owner's is known, but at least one live task cannot be attributed to
    /// any owner yet, so it may be this owner's own (a gate-parked mint whose creator's root has
    /// not reached this node). Nothing here may touch it, and nothing is minted over it either:
    /// reading an unresolved owner as "nothing covers this" is how a fleet peer's parked task gets
    /// duplicated.
    /// </summary>
    Unattributable,
}

/// <summary>
/// What <see cref="OwnerScopedCoverage.Decide"/> concluded. <see cref="Task"/> is the task that
/// decided it: this owner's newest one for <see cref="ReviewCoverageKind.Own"/>, the newest one
/// this node cannot attribute for <see cref="ReviewCoverageKind.Unattributable"/>, another owner's
/// newest for <see cref="ReviewCoverageKind.Teammate"/>, and null when uncovered.
/// </summary>
internal sealed record ReviewCoverage(ReviewCoverageKind Kind, TaskListItem? Task)
{
    public static readonly ReviewCoverage Uncovered = new(ReviewCoverageKind.Uncovered, null);
}

/// <summary>
/// Which of the tasks covering a pull request this install may treat as its own, by the rule the
/// CLI task commands apply (<see cref="TaskOwnerRule"/>: the holder, else the assignee, else the
/// creator). Both auto-pr-review triggers ask it at every covering-task check, so a teammate's
/// replicated review of the same pull request is never claimed, launched on, attached to, or
/// counted as coverage for a request or mention addressed to this owner (<c>docs/scope.md</c>: a
/// teammate's review of the same pull request is a different owner's work and is never touched).
/// <para>
/// A task this node cannot attribute to a known owner is never this owner's to act on, because
/// <see cref="TaskOwnerRule"/> reads an unresolvable fact as an unknown owner and never lets a
/// later fact answer for it, but it is not known to be another owner's either: it may be this
/// owner's own task whose creator's root has not arrived, so a request treats it as coverage
/// rather than minting a twin (<see cref="ReviewCoverageKind.Unattributable"/>). A node whose own owner has no root fingerprint yet is the one
/// exception: it has no team a task could belong to instead, so every task on it is its own, which
/// is what <c>TaskOwnerGuard</c> already does for the CLI. The owner-role override a human may use
/// from the CLI never applies here: nothing the daemon does on its own may act on another owner's
/// task, whatever role this install's owner holds.
/// </para>
/// </summary>
internal static class OwnerScopedCoverage
{
    /// <summary>What a row carries when nothing was read for it, because a node with no root of its own owns every task regardless.</summary>
    private static readonly TaskOwnerFacts UnreadFacts =
        new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Unresolved);

    public static bool IsOwn(string? thisOwnerRoot, TaskOwnerFacts facts) =>
        string.IsNullOrEmpty(thisOwnerRoot) || TaskOwnerRule.Decide(thisOwnerRoot, facts).MayAct;

    /// <summary>Whether the rule could not name an owner for the task at all, as opposed to naming another one.</summary>
    private static bool IsUnattributable(string? thisOwnerRoot, TaskOwnerFacts facts) =>
        !string.IsNullOrEmpty(thisOwnerRoot)
        && TaskOwnerRule.Decide(thisOwnerRoot, facts).Outcome == TaskOwnerOutcome.Unknown;

    /// <summary>
    /// This owner's newest task among <paramref name="candidates"/>, which a caller may order any
    /// way: the answer is the newest by <see cref="TaskListItem.AddedAt"/>, ties broken by id so
    /// two sweeps over the same rows name the same task.
    /// </summary>
    public static TaskListItem? NewestOwn(string? thisOwnerRoot, IEnumerable<CoveringTaskCandidate> candidates) =>
        Newest(candidates.Where(candidate => IsOwn(thisOwnerRoot, candidate.Facts)));

    /// <summary>
    /// This owner's newest task among <paramref name="candidates"/>, or failing that the newest one
    /// this node cannot attribute to any owner: the tasks that may be this owner's own. What a
    /// request's re-mint guard asks, because a closed review that may be this owner's earlier one
    /// must not be minted over.
    /// </summary>
    public static TaskListItem? NewestMaybeOwn(string? thisOwnerRoot, IEnumerable<CoveringTaskCandidate> candidates) =>
        Newest(candidates.Where(candidate =>
            IsOwn(thisOwnerRoot, candidate.Facts) || IsUnattributable(thisOwnerRoot, candidate.Facts)));

    /// <summary>
    /// The decision over the live tasks covering one pull request: this owner's newest wins however
    /// old it is beside a teammate's, then a task this node cannot attribute, then a teammate's,
    /// and nothing live is uncovered.
    /// </summary>
    public static ReviewCoverage Decide(string? thisOwnerRoot, IReadOnlyList<CoveringTaskCandidate> live) =>
        (NewestOwn(thisOwnerRoot, live),
            Newest(live.Where(candidate => IsUnattributable(thisOwnerRoot, candidate.Facts))),
            Newest(live)) switch
        {
            ({ } own, _, _) => new ReviewCoverage(ReviewCoverageKind.Own, own),
            (null, { } unattributable, _) => new ReviewCoverage(ReviewCoverageKind.Unattributable, unattributable),
            (null, null, { } teammate) => new ReviewCoverage(ReviewCoverageKind.Teammate, teammate),
            _ => ReviewCoverage.Uncovered,
        };

    /// <summary>
    /// Reads the ownership facts of each row the way the board and the CLI commands read them
    /// (<see cref="TaskListItemOwnerFacts"/>), and this install's own root. Nothing is read for an
    /// empty list, so the common sweep with nothing covering a pull request pays nothing.
    /// </summary>
    public static async Task<(string? ThisOwnerRoot, IReadOnlyList<CoveringTaskCandidate> Candidates)> ReadAsync(
        IQuerySession session, Guid thisOwnerId, IReadOnlyList<TaskListItem> rows, CancellationToken cancellationToken)
    {
        string? thisOwnerRoot = await OwnerRootFingerprintResolver.ResolveAsync(session, thisOwnerId, cancellationToken);
        if (rows.Count == 0 || string.IsNullOrEmpty(thisOwnerRoot))
        {
            return (thisOwnerRoot, [.. rows.Select(row => new CoveringTaskCandidate(row, UnreadFacts))]);
        }

        Dictionary<Guid, string?> rootsByOwnerId = (await session.Query<OwnerDetails>().ToListAsync(cancellationToken))
            .ToDictionary(owner => owner.Id, owner => owner.RootFingerprint);
        Guid[] creatorTaskIds = [.. rows.Where(TaskListItemOwnerFacts.NeedsCreator).Select(row => row.Id)];
        IReadOnlyDictionary<Guid, OwnerRootFact> creators = await TaskOwnerFactsReader.ReadCreatorsAsync(
            session, creatorTaskIds, ownerId => rootsByOwnerId.GetValueOrDefault(ownerId), cancellationToken);

        return (
            thisOwnerRoot,
            [.. rows.Select(row => new CoveringTaskCandidate(
                row,
                TaskListItemOwnerFacts.From(
                    row, ownerId => rootsByOwnerId.GetValueOrDefault(ownerId), creators.GetValueOrDefault(row.Id))))]);
    }

    private static TaskListItem? Newest(IEnumerable<CoveringTaskCandidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.Task.AddedAt)
            .ThenByDescending(candidate => candidate.Task.Id)
            .Select(candidate => candidate.Task)
            .FirstOrDefault();
}
