using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human asked for this task to be published as a card in an external system
/// (h9k task push-to-jira, backlog 18). It records the request and nothing about the card,
/// because the platform does not author cards: the daemon turns this into an agent session,
/// and what an issue type is, which fields are required, and where the card is routed are the
/// project's own rules, delivered by its repo skills and the agent's MCP access.
/// <para>
/// ProjectKey is the board binding as it stood when the request was made (Project settings),
/// carried on the event rather than read later so the session's instructions and the record of
/// what was asked cannot drift apart. It may be None: a project with no board bound still
/// publishes, and the agent is told to ask its skills where the card belongs.
/// </para>
/// </summary>
public sealed record WorkItemPublicationRequested(
    Guid Id,
    WorkItemProvider Provider,
    JiraProjectKey ProjectKey,
    DateTimeOffset RequestedAt,
    Guid RequestedByOwnerId);
