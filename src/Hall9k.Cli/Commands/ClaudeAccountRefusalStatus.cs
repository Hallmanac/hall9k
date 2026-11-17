using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A claude-code connection whose account the daemon refused, and the projects on this node bound to it.
/// Read fresh from <see cref="ConnectionDetails"/>, the same reasoning <see cref="LaunchHoldStatus"/> gives:
/// the refusal is event-sourced state the daemon records the moment a check fails, and clears the moment one
/// passes, so there is no freshness question to answer.
/// </summary>
internal sealed record ClaudeAccountRefusalStatus(ConnectionDetails Connection, IReadOnlyList<string> ProjectNames)
{
    /// <summary>
    /// The one line <c>h9k status</c> prints per refused connection: what the check found, which already names
    /// the directory and its fix, and what is waiting on it. The daemon re-checks on its own, so the fix is all
    /// the human has to do.
    /// </summary>
    public string NeedsYouLine =>
        $"[red bold]NEEDS YOU[/] [red]Claude Code connection {DomainId.Short(Connection.Id)} "
        + $"({ExternalText.OneLineMarkup(Connection.ClaudeCodeEmail ?? Connection.ExternalAccountId)}) was refused, so no new "
        + $"work starts for {ProjectList} and their held sessions wait. "
        + $"{ExternalText.OneLineMarkup(Connection.ClaudeCodeRefusedReason ?? "The account check failed.")} "
        + "The daemon re-checks on its own and resumes the held sessions once the check passes.[/]";

    private string ProjectList => ProjectNames.Count == 0
        ? "no project yet"
        : string.Join(", ", ProjectNames.Select(name => ExternalText.OneLineMarkup(name)));

    /// <summary>Every refused claude-code connection on this node, oldest first, with the projects bound to it.</summary>
    public static async Task<IReadOnlyList<ClaudeAccountRefusalStatus>> ReadAsync(
        IQuerySession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConnectionDetails> refused =
        [
            .. (await ClaudeCodeAccounts.ListAsync(session, cancellationToken)).Where(connection => connection.ClaudeCodeRefused),
        ];
        if (refused.Count == 0)
        {
            return [];
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>()
            .Where(project => project.ClaudeCodeConnectionId != null)
            .ToListAsync(cancellationToken);
        return
        [
            .. refused.Select(connection => new ClaudeAccountRefusalStatus(
                connection,
                [.. projects.Where(project => project.ClaudeCodeConnectionId == connection.Id).Select(project => project.Name).Order()])),
        ];
    }
}
