using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// One look at a pull request this install does not own — the stacked parent a reviewer's node
/// stands on without ever holding its run (task: a stacked child can stand on a pull request
/// another install owns). Every field is what the provider reported, never inferred: a look that
/// could not be made comes back as <see cref="RemoteParentState.Unknown"/> with the failure in
/// <see cref="Detail"/>, and a number the repository has no pull request for comes back as
/// <see cref="RemoteParentState.Absent"/>, which is a different fact.
/// </summary>
/// <param name="HeadBranch">The branch the pull request opens FROM; blank when there was none to read.</param>
/// <param name="HeadCommit">That branch's head commit; blank when unreported.</param>
/// <param name="BaseBranch">The branch the pull request opens INTO; blank when unreported.</param>
/// <param name="Url">The pull request's own URL; blank when unreported.</param>
/// <param name="LinkedWorkItem">
/// The issue or tracker item the pull request says it closes, or null when it names none — which is
/// an ordinary shape rather than a gap.
/// </param>
/// <param name="Detail">What was observed, in a sentence a caller's own message can carry.</param>
public sealed record RemoteParentRead(
    RemoteParentState State,
    string HeadBranch,
    string HeadCommit,
    string BaseBranch,
    string Url,
    ExternalReference? LinkedWorkItem,
    string Detail)
{
    /// <summary>Nothing was observed, and why — never mistaken for a pull request that does not exist.</summary>
    public static RemoteParentRead Unobserved(string detail) =>
        new(RemoteParentState.Unknown, string.Empty, string.Empty, string.Empty, string.Empty, null, detail);
}

/// <summary>
/// The seam onto the provider for a pull request identified by number alone (gh in production, a
/// fake in tests). Deliberately separate from <see cref="IPullRequestInspector"/>, whose every
/// method takes the URL of a pull request THIS install opened and reads the reviews, checks and
/// merge state that only its own closeout cares about. A stacked parent is somebody else's pull
/// request: all this install knows about it is the number a human declared, and all it needs back
/// is where the branch is and whether the pull request is still open.
/// </summary>
public interface IRemoteParentReader
{
    /// <summary>
    /// Reads pull request <paramref name="pullRequestNumber"/> in the repository at
    /// <paramref name="repositoryPath"/>. Never throws for a pull request that is missing or a
    /// provider that would not answer — both are states this returns, because the caller's whole
    /// job is telling those two apart (AGENTS.md's never-guess rule).
    /// </summary>
    Task<RemoteParentRead> ReadAsync(
        string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken);
}
