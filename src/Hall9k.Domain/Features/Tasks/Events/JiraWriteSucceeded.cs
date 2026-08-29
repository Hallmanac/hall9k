namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The outcome of a write twg carried out and the executor then read back and verified —
/// <see cref="IssueKey"/> is what Jira answered when read back, never what a create or an update
/// call merely claimed. Ends the write this <see cref="WriteId"/> names; a create's success is
/// what makes <c>Hall9k.Cli.Commands.TaskWriteJiraCommand</c> also record
/// <see cref="WorkItemLinked"/>, so a card hall9k created is linked exactly the way one an agent
/// reported through <c>h9k task link-jira</c> always has been.
/// </summary>
public sealed record JiraWriteSucceeded(
    Guid TaskId,
    Guid WriteId,
    string IssueKey,
    string Summary,
    DateTimeOffset SucceededAt);
