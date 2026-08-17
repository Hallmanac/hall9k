using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Handlers;

public static class ProjectDecider
{
    public static ProjectRegistered Register(
        Guid id,
        Guid ownerId,
        Guid connectionId,
        string name,
        string repositoryPath,
        Uri? repositoryUrl,
        string? baseBranch,
        DateTimeOffset registeredAt)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("A project requires a name.");
        }

        if (repositoryPath.IsBlank())
        {
            throw new DomainValidationException("A project requires the local repository path the daemon creates worktrees from.");
        }

        if (connectionId == Guid.Empty)
        {
            throw new DomainValidationException("A project binds to a connection, never to \"the machine's GitHub\" (PLAN.md §10).");
        }

        return new ProjectRegistered(
            id,
            ownerId,
            connectionId,
            name,
            repositoryPath,
            repositoryUrl,
            baseBranch.IsBlank() ? "main" : baseBranch,
            registeredAt);
    }

    public static ProjectSettingsChanged ChangeSettings(
        ProjectAggregate project,
        Optional<IReadOnlyList<VerifyCommand>> verifyCommands,
        Optional<bool> skipPermissions,
        Optional<int> maxParallelAgents,
        Optional<IReadOnlyList<ContextLink>> contextLinks,
        DateTimeOffset changedAt,
        Guid changedByOwnerId)
    {
        if (maxParallelAgents.HasValue && maxParallelAgents.Value < 1)
        {
            throw new DomainValidationException("MaxParallelAgents must be at least 1.");
        }

        return new ProjectSettingsChanged(
            project.Id,
            verifyCommands,
            skipPermissions,
            maxParallelAgents,
            contextLinks,
            changedAt,
            changedByOwnerId);
    }
}
