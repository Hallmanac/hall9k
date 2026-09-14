namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Which events a replicated node ever needs to see (idea 202383dc, ruled 2026-09-12 and
/// 2026-09-13): only <see cref="ProjectScoped"/> ever travels; <see cref="NodeScoped"/> and
/// <see cref="OwnerScoped"/> events stay on the node that appended them.
/// </summary>
public enum EventScope
{
    /// <summary>Tasks, ideas, a run's own work facts, and the team part of project settings.</summary>
    ProjectScoped,

    /// <summary>Run mechanics, sessions, messages sent and read, holds, tokens, connections.</summary>
    NodeScoped,

    /// <summary>An owner's own cross-node identity, on the Owner stream.</summary>
    OwnerScoped,
}
