namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The platform pushed the run's branch, and these are the two heads it saw (task: a
/// review-feedback lap's fix reply posts only after the platform's push has moved the pull
/// request's head). <see cref="StartedFrom"/> is the tip origin held for the branch immediately
/// before the push and <see cref="PushedTip"/> the tip the push left, so the pair says whether this
/// push changed the pull request's head, whoever else had moved it earlier.
/// <para>
/// Recorded on the run, once, before any held fix reply posts: a push step that runs again for the
/// same run (a daemon restart, a post that failed) reads origin's tip as the one this run's own
/// first push left, so only the pair recorded here can still tell that the first push moved the
/// head. The first record wins.
/// </para>
/// </summary>
/// <param name="StartedFrom">Origin's tip for the branch just before the push.</param>
/// <param name="PushedTip">The local branch's tip the push sent.</param>
public sealed record RunBranchPushed(
    Guid Id,
    string StartedFrom,
    string PushedTip,
    DateTimeOffset PushedAt);
