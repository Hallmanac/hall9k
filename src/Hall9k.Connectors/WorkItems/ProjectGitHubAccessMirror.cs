using System.Text.Json;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Connectors.WorkItems;

/// <summary>This install's own role on a project's repository, plus the repository's own <c>owner/repo</c> name, as just observed.</summary>
public sealed record ProjectGitHubAccessResult(GitHubRepositoryRole OwnRole, string Repository);

/// <summary>
/// Hall9k's own read-only mirror of a project's GitHub repository access (idea 202383dc, A2b,
/// item 3): this install's own role, read from the repository object itself and so obtainable at
/// any access level, always; the full collaborator list with roles, read only when this install's
/// own account already has push — GitHub itself refuses to list collaborators to anyone who does
/// not. Every fact is appended to the project's own stream through
/// <see cref="ProjectDecider.ObserveGitHubAccess"/>/<see cref="ProjectDecider.ObserveGitHubCollaborators"/>
/// only when it actually changed since the last observation; nothing here ever writes a permission
/// back to GitHub.
/// <para>
/// The collaborator read caps at 100 entries (<c>per_page=100</c>) rather than paging further — a
/// deliberate, explicit limit rather than an invisible one, the same call
/// <see cref="GitHubReviewAssignments.ListReviewRequestedAsync"/>'s own 500-item cap already makes
/// for a different list: a repository with more collaborators than that is a later problem, and
/// v0 would rather cap visibly than page silently forever.
/// </para>
/// </summary>
public sealed class ProjectGitHubAccessMirror(ProjectGitHubClient? client = null)
{
    private readonly ProjectGitHubClient client = client ?? new ProjectGitHubClient();

    public async Task<ProjectGitHubAccessResult> ObserveAsync(
        IDocumentSession session, ProjectDetails project, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        ProjectGitHubAccount account = await ProjectGitHubClient.ResolveAccountAsync(session, project, cancellationToken);

        ProcessResult repoView;
        try
        {
            repoView = await client.RunAsync(
                account, project.RepositoryPath, ["repo", "view", "--json", "viewerPermission,nameWithOwner"], cancellationToken);
        }
        // ProjectGitHubClient.RunAsync itself no longer translates this (independent pre-PR
        // review, cycle 1, adversarial lens): every other caller reaches it through
        // ProjectScopedGitHubRunner, and each of those connectors already turns a hang into its
        // own richer message. This call — standalone h9k project join's own read of this
        // install's role — is the one caller with no handling of its own, so a hung gh would
        // otherwise escape as a raw TimeoutException (or the ProcessOutputStuckException that
        // derives from it) and print a stack trace instead of the reason on stderr AGENTS.md's
        // CLI standard requires.
        catch (TimeoutException exception)
        {
            throw new DomainValidationException(
                $"gh repo view did not answer from {project.RepositoryPath}: {exception.Message}");
        }

        if (repoView.ExitCode != 0)
        {
            throw new DomainValidationException(
                $"gh could not read {project.Name}'s repository to observe this install's own access "
                + $"(gh repo view --json viewerPermission,nameWithOwner from {project.RepositoryPath}): "
                + $"{repoView.StandardError.Trim()}");
        }

        (GitHubRepositoryRole role, string repository) = ParseRepoView(repoView.StandardOutput, project);

        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(project.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No project {project.Id}.");

        if (ProjectDecider.ObserveGitHubAccess(aggregate, account.Id, account.Login, role, observedAt) is { } ownObserved)
        {
            session.Events.Append(project.Id, ownObserved);
        }

        if (role.HasPush)
        {
            IReadOnlyList<GitHubCollaboratorRole>? collaborators =
                await TryReadCollaboratorsAsync(account, project.RepositoryPath, repository, cancellationToken);
            if (collaborators is not null
                && ProjectDecider.ObserveGitHubCollaborators(aggregate, collaborators, observedAt) is { } collaboratorsObserved)
            {
                session.Events.Append(project.Id, collaboratorsObserved);
            }
        }

        return new ProjectGitHubAccessResult(role, repository);
    }

    /// <summary>
    /// Best-effort: a collaborator list this install's own account cannot see (GitHub answers 403
    /// below push, which <see cref="ObserveAsync"/> already gates on, but a repository can also
    /// disable the endpoint for other reasons) leaves the last observation exactly as it was rather
    /// than failing the whole access observation over a fact this install genuinely cannot read.
    /// </summary>
    private async Task<IReadOnlyList<GitHubCollaboratorRole>?> TryReadCollaboratorsAsync(
        ProjectGitHubAccount account, string workingDirectory, string repository, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await client.RunAsync(
                account, workingDirectory, ["api", $"repos/{repository}/collaborators?per_page=100"], cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            return ParseCollaborators(result.StandardOutput);
        }
        // A response that parses as JSON but not as the expected shape (or does not parse as JSON
        // at all) is exactly as unreadable as a non-zero exit — this method's whole contract is
        // best-effort, and a malformed collaborator list must not abort the access observation
        // this call sits inside (ObserveAsync still records this install's own role either way).
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (GitHubRepositoryRole Role, string Repository) ParseRepoView(string json, ProjectDetails project)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("viewerPermission", out JsonElement permissionElement)
                && permissionElement.ValueKind == JsonValueKind.String
                && permissionElement.GetString() is { } permission
                && root.TryGetProperty("nameWithOwner", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                && nameElement.GetString() is { } repository)
            {
                return (permission, repository);
            }
        }
        // Valid JSON that simply does not carry the expected shape (an error body, a field of the
        // wrong type) is exactly as unreadable as malformed JSON — GetString() throws
        // InvalidOperationException rather than returning false for a property of the wrong kind,
        // so both exception types land here rather than only the JSON-syntax one (the same defect
        // class NodeBootstrap.ParseGhIdentity shipped with and fixed in this same self-review).
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
        }

        throw new DomainValidationException(
            $"gh reported no readable role for {project.Name}'s repository "
            + $"(gh repo view --json viewerPermission,nameWithOwner printed: {json.Trim()}).");
    }

    /// <summary>
    /// Split from the gh call so it is unit-testable against recorded gh output, mirroring every
    /// other GitHub-JSON mapper in this codebase. Every field is checked for its expected kind
    /// before being read, so one malformed entry is skipped rather than raising an exception that
    /// would otherwise abort collecting every other, valid entry in the same response.
    /// </summary>
    internal static IReadOnlyList<GitHubCollaboratorRole> ParseCollaborators(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        List<GitHubCollaboratorRole> collaborators = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out long id)
                && item.TryGetProperty("login", out JsonElement loginElement) && loginElement.ValueKind == JsonValueKind.String
                && loginElement.GetString() is { } login
                && item.TryGetProperty("role_name", out JsonElement roleElement) && roleElement.ValueKind == JsonValueKind.String
                && roleElement.GetString() is { } roleName)
            {
                collaborators.Add(new GitHubCollaboratorRole(id, login, roleName));
            }
        }

        return collaborators;
    }
}
