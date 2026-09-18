using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// The branch to hand <see cref="Handlers.TaskDecider"/>'s own claim methods as
/// <c>resumesBranch</c> (idea 202383dc, piece C's residual, criterion 1): this task's own latest
/// run — by <see cref="RunDetails.DispatchedAt"/> — when it was dispatched by a real node OTHER
/// than <paramref name="claimingNodeId"/> and still names a branch. Null for every other shape: no
/// run yet, the latest run carries the interactive/deliberate <see cref="Guid.Empty"/> sentinel (a
/// human's own <c>h9k task work</c> or <c>h9k task start</c> — never a foreign NODE's work,
/// whichever machine it ran on), or the latest run is the claiming node's own.
/// <para>
/// Lives in the domain, shared by <c>DispatchEngine.TryClaimAsync</c> and the CLI's own two
/// interactive claims (<c>h9k task work</c>, <c>h9k task start</c>), the same three-caller shape
/// <see cref="StackedBaseResolver"/> already has, because a foreign node's own branch must resume
/// no matter which of the three doors reclaims the task — an interactive claim shares this
/// branch's fate with the daemon's own claim (independent pre-PR review, cycle 1, conformance
/// lens).
/// </para>
/// <para>
/// <paramref name="claimingNodeId"/> is <see cref="Guid.Empty"/> for both CLI callers, since an
/// interactive or deliberate claim carries that same sentinel and has no real node identity of its
/// own to exclude — every real node's own latest run counts as foreign to it.
/// </para>
/// </summary>
public static class ForeignResumeBranchResolver
{
    public static async Task<string?> ResolveAsync(
        IQuerySession session, TaskAggregate task, Guid taskId, Guid claimingNodeId, CancellationToken cancellationToken)
    {
        if (task.Type == TaskType.PrReview)
        {
            return null;
        }

        RunDetails? latestRun = await session.Query<RunDetails>()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.DispatchedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return latestRun is not null
            && latestRun.NodeId != Guid.Empty
            && latestRun.NodeId != claimingNodeId
            && latestRun.Branch.IsNotBlank()
            ? latestRun.Branch
            : null;
    }
}
