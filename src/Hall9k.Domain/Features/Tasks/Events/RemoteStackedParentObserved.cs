namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// One look at the pull request this task declared itself stacked on, taken on the closeout
/// watcher's own cadence (task: a stacked child can stand on a pull request another install owns).
/// The child's whole knowledge of its parent, because a reviewer's node never holds the parent's
/// run: there is no local task to read a state off, so what GitHub said about that pull request —
/// and when — is the record.
/// <para>
/// Appended only when the look said something new. A sweep that re-observes an unchanged pull
/// request writes nothing: the stream is a record of observations, and re-recording the identical
/// one every few minutes would bury the moment the parent actually moved (the same discipline
/// <see cref="TaskDependencyFailed"/>'s own same-reason guard applies).
/// </para>
/// </summary>
/// <param name="PullRequestNumber">
/// The parent's number, carried on the event rather than only on the task, so a replay can tell an
/// observation of the declared parent from one written before a revision repointed the edge.
/// </param>
/// <param name="State">What the provider said the pull request is — never inferred from anything else.</param>
/// <param name="HeadBranch">
/// The branch the pull request is opening FROM, which is what the child's worktree and branch are
/// cut from and what its own pull request targets. Blank when the look found no pull request to
/// read one off.
/// </param>
/// <param name="HeadCommit">That branch's head at this look, or blank when unreported.</param>
/// <param name="BaseBranch">
/// The branch the pull request is opening INTO — read rather than assumed to be the project's own,
/// because a parent that is itself stacked merges somewhere else and a child cannot be retargeted
/// onto the project's base from there (the same distinction <c>StackedParentVerdict.ParentMergedElsewhere</c>
/// exists for).
/// </param>
/// <param name="Url">The pull request's own URL as the provider reported it, or blank when there is none to report.</param>
/// <param name="LinkedWorkItem">
/// The issue or tracker item this pull request says it closes, or null when it names none — which
/// is an ordinary, supported shape rather than a gap (Brian's ruling, 2026-09-07: the edge is
/// declared by pull request number precisely because not every repository tracks its backlog in
/// GitHub issues). When a local task carries the same external reference, <c>h9k task show</c>
/// names it; nothing requires one to exist.
/// </param>
/// <param name="Detail">What was observed, in a sentence for the log and the task surface.</param>
public sealed record RemoteStackedParentObserved(
    Guid Id,
    int PullRequestNumber,
    RemoteParentState State,
    string HeadBranch,
    string HeadCommit,
    string BaseBranch,
    string Url,
    ExternalReference? LinkedWorkItem,
    string Detail,
    DateTimeOffset ObservedAt);
