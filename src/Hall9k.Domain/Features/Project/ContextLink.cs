namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Named pointer injected into every agent's context for this project ("jira", "wiki",
/// "staging"). The agent follows it itself via MCP, gh, or fetching — no connector needed.
/// </summary>
public sealed record ContextLink(string Name, Uri Url);
