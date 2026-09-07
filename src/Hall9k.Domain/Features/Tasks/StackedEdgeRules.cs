namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The one place that answers "does this blocker hold this dependent back, and is it dead" for
/// both kinds of dependency edge (task: a stacked pull-request edge exists as an explicit opt-in
/// dependency). Two callers ask — <see cref="Handlers.TaskDecider.Assign"/>, computing the unmet
/// set an assignment freezes, and <see cref="Handlers.TaskDependencyResolver"/>, re-evaluating it
/// every sweep — and they must never disagree, or an assignment would freeze a set the resolver
/// then reads by a different rule.
/// <para>
/// A plain blocked-by edge behaves exactly as it always has: it waits for true closeout, and it is
/// dead the moment closeout can no longer arrive (Decisions Log #34). Only an edge a human
/// explicitly declared stacked reads Delivered as enough (Brian's cohesion ruling, 2026-08-28: the
/// tool never infers stacking from an ordinary blocked-by).
/// </para>
/// </summary>
public static class StackedEdgeRules
{
    /// <summary>Whether <paramref name="dependency"/> still holds <paramref name="task"/> back.</summary>
    public static bool Blocks(TaskAggregate task, TaskDependency dependency) =>
        Blocks(task.StackedOnTaskId, dependency);

    /// <summary>
    /// Whether <paramref name="dependency"/> can no longer reach the bar this edge waits on, so
    /// <paramref name="task"/> needs a human rather than more patience.
    /// </summary>
    public static bool IsDead(TaskAggregate task, TaskDependency dependency) =>
        IsDead(task.StackedOnTaskId, dependency);

    /// <summary>
    /// The same rule for a caller holding the dependent's <em>projection</em> rather than its
    /// aggregate — <c>h9k task show</c>, which renders the very screen that explains what "met"
    /// means. <paramref name="stackedOnTaskId"/> is <c>TaskDetails.StackedOnTaskId</c>: null when
    /// no edge is declared, which is every unstacked task and therefore the unchanged rule.
    /// </summary>
    public static bool Blocks(Guid? stackedOnTaskId, TaskDependency dependency) =>
        stackedOnTaskId == dependency.Id ? dependency.BlocksStackedChild : dependency.Blocks;

    /// <summary><see cref="Blocks(Guid?, TaskDependency)"/>'s twin, for the same callers.</summary>
    public static bool IsDead(Guid? stackedOnTaskId, TaskDependency dependency) =>
        stackedOnTaskId == dependency.Id ? dependency.IsDeadForStackedChild : dependency.IsDead;
}
