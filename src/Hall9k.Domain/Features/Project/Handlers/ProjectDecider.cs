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
        DateTimeOffset registeredAt,
        ProjectHome? homeDirectory = null)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("A project requires a name.");
        }

        if (repositoryPath.IsBlank())
        {
            throw new DomainValidationException("A project requires the local repository path the daemon creates worktrees from.");
        }

        RefuseRelativeRepositoryPath(repositoryPath);

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
            registeredAt,
            homeDirectory ?? ProjectHome.None);
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
        Optional<ReviewRerequestPolicy> reviewRerequest = default,
        Optional<JiraProjectKey> jiraProjectKey = default,
        Optional<ProjectHome> homeDirectory = default,
        Optional<string> repositoryPath = default)
    {
        if (repositoryPath.HasValue)
        {
            if (repositoryPath.Value.IsBlank())
            {
                throw new DomainValidationException(
                    "A project always has a local repository path the daemon creates worktrees from; "
                    + "there is no clearing it. Point it somewhere else instead.");
            }

            RefuseRelativeRepositoryPath(repositoryPath.Value);
        }

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
        // else must be spawnable: the value reaches the executor's shell command line, so a
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
            reviewRerequest,
            jiraProjectKey,
            homeDirectory,
            repositoryPath);
    }

    /// <summary>
    /// The repository path carries the same rule <see cref="ProjectHome"/> carries, and for the
    /// same reason: it is recorded once and read back by the daemon, which runs in no particular
    /// directory, so a relative path names a different repository for every process that resolves
    /// it. Callers resolve relative input themselves, where the current directory still means
    /// something; what reaches here unrooted is refused rather than recorded.
    /// <para>
    /// Origin incident (2026-08-23): the pre-PR review of the project-home branch found
    /// <c>h9k project add --no-home --repo-url …</c> composing the path from an empty home and
    /// recording <c>repo/&lt;name&gt;.git</c>, which the daemon would have resolved against its
    /// own working directory. The CLI refuses that combination now; this is the rule underneath
    /// it, so no other caller can reintroduce the same shape.
    /// </para>
    /// </summary>
    private static void RefuseRelativeRepositoryPath(string repositoryPath)
    {
        if (!Path.IsPathRooted(repositoryPath))
        {
            throw new DomainValidationException(
                $"'{repositoryPath}' is not an absolute path. A project's repository path is recorded "
                + "once and read back by the daemon, which runs in no particular directory, so a "
                + "relative path would name a different repository for every caller. Pass a full path.");
        }
    }
}
