namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One project in a solution, named by the project key (the 26-character ULID from the ledger
/// genesis, identical on every node) and never by this node's own project id, with the name and
/// repository URL a node that has not joined the project can still show for it.
/// </summary>
public sealed record SolutionMember(string ProjectKey, string Name, Uri RepositoryUrl);
