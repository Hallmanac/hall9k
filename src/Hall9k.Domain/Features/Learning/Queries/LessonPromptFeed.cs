using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Features.Learning.Queries;

/// <summary>
/// The one read every prompt-composing caller makes to get its lesson section (idea d805fd8b,
/// piece 5; backlog 55): a project's active lessons plus this owner's, marked against this node's
/// own identity, bounded by this node's configured caps.
/// <para>
/// One entry point rather than four, and in the Domain rather than beside any one builder,
/// because the callers sit on opposite sides of the reference graph and must not be able to
/// disagree: <c>RunLauncher</c> and <c>ReviewEngine</c> in the daemon, and <c>h9k task work</c>
/// in the CLI process, which cannot reference the daemon at all. A second copy of the scope
/// predicate or the provenance filter is exactly the shape that drifts with no build failure to
/// catch it, the same argument <c>KnowledgeDocumentText</c> already makes for the two renderers.
/// </para>
/// <para>
/// The caps are read from the platform config file on every call rather than bound once at daemon
/// startup, the same "no daemon process holds this" reading
/// <see cref="OperatingSettings.InteractiveClaimStaleAfterDays"/> and
/// <see cref="OperatingSettings.InviteExpiryHours"/> already get: a change takes effect at the
/// next prompt composition instead of the next restart, and the interactive CLI path resolves it
/// through this identical code rather than through <c>DaemonOptions</c>, which it structurally
/// cannot reach.
/// </para>
/// </summary>
public static class LessonPromptFeed
{
    /// <summary>
    /// The section for <paramref name="projectId"/>, or <see cref="InjectedLessons.None"/> when
    /// there is nothing to carry.
    /// <para>
    /// Never throws for a missing node row or an unreadable config file: this runs on the dispatch
    /// path, and a run whose prompt is missing its lessons section is a worse run, not a failed
    /// one. An install whose <see cref="NodeDetails"/> row is not there yet resolves no node id
    /// and no owner, which means no owner-scoped lesson and every agent-recorded lesson marked
    /// <see cref="LessonProvenanceMark.AgentOnUnobservedNode"/> and held back: honest, and
    /// self-correcting the moment the row exists.
    /// </para>
    /// </summary>
    public static async Task<InjectedLessons> LoadAsync(
        IQuerySession query, Guid projectId, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await query.Query<NodeDetails>()
            .Where(candidate => candidate.MachineName == machineName)
            .Take(1)
            .ToListAsync(cancellationToken)).FirstOrDefault();

        OperatingSettings configured = (await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken)).Settings;
        LessonInjectionCaps caps = LessonInjectionCaps.Resolve(
            configured.LessonPromptMaxLessons, configured.LessonPromptMaxCharacters);

        return await ComposeAsync(
            query, projectId, node?.OwnerId ?? Guid.Empty, node?.Id ?? Guid.Empty, caps, cancellationToken);
    }

    /// <summary>
    /// The seam the tests drive: identity and caps handed in rather than read off the host, so
    /// every rule the section holds is provable without a machine name, a config file, or a
    /// registered node (the testing rule, Brian 2026-09-13).
    /// <para>
    /// The node this asks about is always the node COMPOSING the prompt, never the node recorded
    /// on the run being dispatched. The two differ on a resumed foreign-node branch (idea
    /// 202383dc), and it is the composing node that matters: the question the provenance mark
    /// answers is "did an agent this install controls write this", which is about the machine
    /// assembling the instructions rather than about whose run the branch started as.
    /// </para>
    /// </summary>
    public static async Task<InjectedLessons> ComposeAsync(
        IQuerySession query,
        Guid projectId,
        Guid ownerId,
        Guid thisNodeId,
        LessonInjectionCaps caps,
        CancellationToken cancellationToken)
    {
        string projectScope = KnowledgeScope.Project;
        string active = LearningStatus.Active;
        IReadOnlyList<LearningDetails> projectLessons = projectId == Guid.Empty
            ? []
            : await query.Query<LearningDetails>()
                .Where(lesson => lesson.MatchesSql("d.data ->> 'scope' = ?", projectScope))
                .Where(lesson => lesson.ScopeId == projectId)
                .Where(lesson => lesson.MatchesSql("d.data ->> 'status' = ?", active))
                .ToListAsync(cancellationToken);

        string ownerScope = KnowledgeScope.Owner;
        IReadOnlyList<LearningDetails> ownerLessons = ownerId == Guid.Empty
            ? []
            : await query.Query<LearningDetails>()
                .Where(lesson => lesson.MatchesSql("d.data ->> 'scope' = ?", ownerScope))
                .Where(lesson => lesson.ScopeId == ownerId)
                .Where(lesson => lesson.MatchesSql("d.data ->> 'status' = ?", active))
                .ToListAsync(cancellationToken);

        return LessonInjection.Compose(projectLessons, ownerLessons, thisNodeId, caps);
    }

    /// <summary>
    /// How many of a project's own lessons are live, whatever their provenance: the number
    /// <c>h9k status</c> compares against the count cap to decide whether to name the
    /// distillation lever. Counted rather than composed because the answer is about the project's
    /// inventory, not about what any one prompt would carry: a project whose lessons are mostly
    /// another node's is over the cap for a reader of <c>lessons.md</c> even when the injected
    /// section is short.
    /// </summary>
    public static async Task<int> CountActiveProjectLessonsAsync(
        IQuerySession query, Guid projectId, CancellationToken cancellationToken)
    {
        string projectScope = KnowledgeScope.Project;
        string active = LearningStatus.Active;
        return await query.Query<LearningDetails>()
            .Where(lesson => lesson.MatchesSql("d.data ->> 'scope' = ?", projectScope))
            .Where(lesson => lesson.ScopeId == projectId)
            .Where(lesson => lesson.MatchesSql("d.data ->> 'status' = ?", active))
            .CountAsync(cancellationToken);
    }
}
