using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Shared.Exceptions;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// How a command that queues an envelope picks the project to queue it under, when
/// <c>--project</c> is optional: the named one, or this node's only messaging-eligible project.
/// <para>
/// Explicit <c>--project</c> resolves the same way every other command's own resolves one (name,
/// fragment, or id — <see cref="ProjectResolver.ResolveAsync"/>), with no eligibility check of its
/// own: a project not yet eligible for messaging still queues fine, since queueing never touches
/// git (idea 202383dc, M1b) — only the daemon's own flush actually needs a repository, and by then
/// the project may well have one. The default-selection path is narrower on purpose: defaulting
/// to, or silently offering, a project the sweep can never actually flush would only ever strand
/// what was queued.
/// </para>
/// </summary>
internal static class EligibleProject
{
    /// <summary><paramref name="purpose"/> completes "pass --project to say which one <em>…</em>",
    /// so each caller's ambiguity error names its own act rather than a generic one.</summary>
    public static async Task<ProjectDetails> ResolveAsync(
        IQuerySession session, string? projectOption, string purpose, CancellationToken cancellationToken)
    {
        if (projectOption.IsNotBlank())
        {
            return await ProjectResolver.ResolveAsync(session, projectOption, cancellationToken);
        }

        IReadOnlyList<ProjectDetails> allProjects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        List<ProjectDetails> eligible = [.. allProjects
            .Where(candidate => candidate.IsEligibleForMessaging())
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)];

        return eligible switch
        {
            [ProjectDetails single] => single,
            [] => throw new DomainValidationException(
                "No eligible projects (not archived, with a repository) are registered yet. Register "
                + "one: h9k project add --name <name> --repo <path>."),
            _ => throw new DomainConflictException(
                $"This node has {eligible.Count} eligible projects — pass --project to say which one "
                + $"{purpose}: {string.Join(", ", eligible.Select(candidate => candidate.Name))}."),
        };
    }
}
