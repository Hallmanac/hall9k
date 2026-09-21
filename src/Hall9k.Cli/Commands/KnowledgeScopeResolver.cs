using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>One resolved recording scope: the vocabulary value, the id it points at, and how to name it to a human.</summary>
internal sealed record ResolvedKnowledgeScope(KnowledgeScope Scope, Guid ScopeId, string Label);

/// <summary>
/// Which project or owner a <c>h9k decide</c> or <c>h9k learn</c> call is recording against
/// (idea d805fd8b, piece 1). Project is the default and <c>--owner</c> is the deliberate act,
/// because the failure is asymmetric: too narrow means one other project misses something, too
/// wide means a wrong statement rides in every prompt everywhere (IDEA-learning-capture, "Scope
/// determines travel").
/// <para>
/// The project comes from <c>--project</c> when it was named, then from the run this call was
/// made inside, then from the sole registered project — the same single-project-install courtesy
/// <c>h9k pr review</c> already extends, and the same refusal when several are registered and
/// nothing said which.
/// </para>
/// </summary>
internal static class KnowledgeScopeResolver
{
    public static async Task<ResolvedKnowledgeScope> ResolveAsync(
        IQuerySession session,
        string? namedProject,
        bool ownerScoped,
        Guid? projectFromRun,
        Guid ownerId,
        string noun,
        CancellationToken cancellationToken)
    {
        if (ownerScoped && namedProject.IsNotBlank())
        {
            throw new DomainValidationException(
                "--owner and --project ask for opposite scopes; pick one. --owner is for a habit that "
                + $"holds wherever you work, --project for a {noun} about one codebase.");
        }

        if (ownerScoped)
        {
            return new ResolvedKnowledgeScope(KnowledgeScope.Owner, ownerId, "you, across every project");
        }

        if (namedProject.IsNotBlank())
        {
            ProjectDetails named = await ProjectResolver.ResolveAsync(session, namedProject, cancellationToken);
            return new ResolvedKnowledgeScope(KnowledgeScope.Project, named.Id, named.Name);
        }

        if (projectFromRun is { } fromRun)
        {
            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(fromRun, cancellationToken);
            return new ResolvedKnowledgeScope(KnowledgeScope.Project, fromRun, project?.Name ?? fromRun.ToString());
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        return projects switch
        {
            [ProjectDetails single] => new ResolvedKnowledgeScope(KnowledgeScope.Project, single.Id, single.Name),
            [] => throw new DomainNotFoundException(
                $"No projects are registered, so there is no project to scope this {noun} to. Register one "
                + $"(h9k project add --name <name> --repo <path>), or record it against yourself with --owner."),
            _ => throw new DomainValidationException(
                $"{projects.Count} projects are registered, so which one this {noun} belongs to cannot be "
                + "inferred. Pass --project <name> (h9k project list shows them), or --owner if it holds "
                + "wherever you work."),
        };
    }
}
