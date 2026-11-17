using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Projections;

namespace Hall9k.Domain.Features.Connection;

/// <summary>
/// Which claude-code connection a piece of work runs under, as a connection id or null for the default
/// login. A run takes the account it froze at dispatch; work that belongs to no run takes its project's
/// current binding; a solution follows its members.
/// </summary>
public static class ClaudeCodeAccountBinding
{
    /// <summary>
    /// The account a run froze at dispatch. A run with nothing recorded was dispatched before the binding
    /// existed and keeps the default login it started under, whatever its project is bound to now.
    /// </summary>
    public static Guid? ForRun(RunDetails run) =>
        run.ClaudeCodeAccountRecorded
            ? run.ClaudeCodeConnectionId
            : null;

    /// <summary>The account a run dispatched now for <paramref name="project"/> freezes, and what its non-run work uses.</summary>
    public static Guid? ForProject(ProjectDetails project) => project.ClaudeCodeConnectionId;

    /// <summary>
    /// The account a pull-request review pre-flight runs under: the frozen account of the run that dispatched
    /// it. The pre-flight is dispatched before that run's own stream is written, so while there is no run yet it
    /// is the binding that run is about to freeze, which is its project's current one. A run that exists and
    /// recorded nothing was dispatched before the binding existed, so its pre-flight keeps the default login.
    /// </summary>
    public static Guid? ForPreflight(RunDetails? dispatchingRun, ProjectDetails project) =>
        dispatchingRun is null
            ? ForProject(project)
            : ForRun(dispatchingRun);

    /// <summary>
    /// The account a feed courier runs under: the current binding of the project its own dispatch names. A
    /// courier runs in its own run directory, never in a project's checkout, so nothing about where it runs says
    /// whose it is. <paramref name="project"/> is that project, or null when it is no longer recorded.
    /// </summary>
    public static Guid? ForCourier(CourierRunDispatched dispatched, ProjectDetails? project) =>
        project is { } named && named.Id == dispatched.ProjectId
            ? ForProject(named)
            : null;

    /// <summary>
    /// A solution has no settings of its own, so it follows its members: their shared account when every
    /// member agrees, and the default login otherwise (including a solution with no members).
    /// </summary>
    public static Guid? ForSolution(IEnumerable<Guid?> memberBindings)
    {
        Guid?[] bindings = [.. memberBindings.Distinct()];
        return bindings is [{ } shared]
            ? shared
            : null;
    }
}
