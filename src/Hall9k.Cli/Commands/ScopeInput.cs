using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The one place the CLI turns a human's scope word into a <see cref="ReplicationScope"/> (idea
/// 8c5993c5) — shared by <see cref="IdeaScopeCommand"/> and <see cref="TaskScopeCommand"/> rather
/// than duplicated in each.
/// </summary>
internal static class ScopeInput
{
    public static ReplicationScope Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "private" => ReplicationScope.Private,
        "fleet" => ReplicationScope.Fleet,
        "team" => ReplicationScope.Team,
        _ => throw new DomainValidationException($"'{value}' is not a scope Hall9k recognizes (private, fleet, or team)."),
    };

    /// <summary>How a scope reads on <c>h9k idea show</c>/<c>h9k task show</c>, colored by how far it travels.</summary>
    public static string Markup(ReplicationScope scope) => scope.Value switch
    {
        "Private" => "[red]Private[/] [dim](this node only)[/]",
        "Fleet" => "[yellow]Fleet[/] [dim](every node this owner runs)[/]",
        "Team" => "[green]Team[/] [dim](every project member's own fleet)[/]",
        _ => scope.Value.Length == 0 ? "[dim]Unknown[/]" : scope.Value,
    };
}
