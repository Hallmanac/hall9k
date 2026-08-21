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
        Guid changedByOwnerId,
        Optional<CommitStyle> commitStyle = default,
        Optional<AgentModel> model = default,
        Optional<ReviewRerequestPolicy> reviewRerequest = default)
    {
        if (maxParallelAgents.HasValue && maxParallelAgents.Value < 1)
        {
            throw new DomainValidationException("MaxParallelAgents must be at least 1.");
        }

        // Unknown is a legal explicit value: it clears the project override so the
        // platform default applies again.
        if (commitStyle.HasValue
            && commitStyle.Value is { } style
            && style != CommitStyle.Unknown
            && style != CommitStyle.Narrative
            && style != CommitStyle.Append)
        {
            throw new DomainValidationException(
                $"CommitStyle must be {CommitStyle.Narrative} or {CommitStyle.Append} "
                + "(how follow-up runs land fixes on the PR branch, Decisions Log #26).");
        }

        // Unknown clears the project override, exactly as it does for CommitStyle. Anything
        // else must be spawnable: the value reaches the executor's /bin/sh command line, so a
        // model carrying shell metacharacters is rejected here rather than quoted and hoped for.
        if (model.HasValue && model.Value is { } chosen && chosen != AgentModel.Unknown && !chosen.IsWellFormed)
        {
            throw new DomainValidationException(
                $"'{chosen.Value}' is not a usable model name. Use a tier alias "
                + $"({AgentModel.Fable}, {AgentModel.Opus}, {AgentModel.Sonnet}, {AgentModel.Haiku}) or an exact "
                + $"model id (for example {AgentModel.PlatformFallback}); letters, digits, and . _ - : / @ [ ] only.");
        }

        // Unknown clears the project override so the owner preference (or the node default)
        // decides again — the same clearing idiom CommitStyle and AgentModel use.
        if (reviewRerequest.HasValue
            && reviewRerequest.Value is { } policy
            && policy != ReviewRerequestPolicy.Unknown
            && policy != ReviewRerequestPolicy.Enabled
            && policy != ReviewRerequestPolicy.Disabled)
        {
            throw new DomainValidationException(
                $"The review re-request policy must be {ReviewRerequestPolicy.Enabled} or "
                + $"{ReviewRerequestPolicy.Disabled} (whether closeout asks the reviewers for another "
                + "pass after a fix follow-up pushes, Decisions Log #62).");
        }

        return new ProjectSettingsChanged(
            project.Id,
            verifyCommands,
            skipPermissions,
            maxParallelAgents,
            contextLinks,
            changedAt,
            changedByOwnerId,
            commitStyle,
            model,
            reviewRerequest);
    }
}
