namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// What a deterministic issue author needs to create one, mirroring
/// <see cref="WorkItemImportRequest"/>'s shape: content the platform already composed (title
/// from the objective, body from the criteria and agent context — issue shape is uniform enough
/// to author without an agent, unlike a Jira card), and the directory <c>gh</c> resolves the
/// destination repository from.
/// </summary>
public sealed record GitHubIssueCreateRequest(
    string Title,
    string? Body,
    IReadOnlyList<string> Labels,
    string WorkingDirectory);
