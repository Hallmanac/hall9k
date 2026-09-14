namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Which events a replicated node ever needs to see (idea 202383dc, ruled 2026-09-12 and
/// 2026-09-13): only <see cref="ProjectScoped"/> ever travels; <see cref="NodeScoped"/> and
/// <see cref="OwnerScoped"/> events stay on the node that appended them.
/// </summary>
public enum EventScope
{
    /// <summary>
    /// Tasks, ideas, epics, a project's own lifecycle, a run's own work facts, and the tokens
    /// recorded against one — never yet the team part of project settings, which today still
    /// ships bundled with the node-local part in one <see cref="NodeScoped"/> event, until M2
    /// splits them apart.
    /// </summary>
    ProjectScoped,

    /// <summary>
    /// Run mechanics, sessions, holds, connections, this node's own identity, and project
    /// settings (team and node parts alike, until M2's split).
    /// </summary>
    NodeScoped,

    /// <summary>An owner's own cross-node identity, on the Owner stream.</summary>
    OwnerScoped,
}
