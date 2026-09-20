using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Spectre.Console;

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

    /// <summary>
    /// How a scope reads on <c>h9k idea show</c>/<c>h9k task show</c>, colored by how far it
    /// travels. The fallback branch escapes: <see cref="ReplicationScope"/>'s implicit string
    /// conversion accepts any value a peer sends (<c>_ =&gt; new ReplicationScope(value, -1)</c>), so
    /// an unrecognized scope replicated onto this document is untrusted input by the time it reaches
    /// here, not merely a display label — rendered raw, an unbalanced or styled value throws
    /// <c>InvalidOperationException</c> out of Spectre's own markup parser or injects arbitrary
    /// styling, the same class of defect <see cref="IdeaShowCommand"/>'s <c>idea.Text.EscapeMarkup()</c>
    /// a few lines above already guards against for idea text (independent pre-PR review, cycle 7,
    /// adversarial lens, medium).
    /// </summary>
    public static string Markup(ReplicationScope scope) => scope.Value switch
    {
        "Private" => "[red]Private[/] [dim](this node only)[/]",
        "Fleet" => "[yellow]Fleet[/] [dim](every node this owner runs)[/]",
        "Team" => "[green]Team[/] [dim](every project member's own fleet)[/]",
        _ => scope.Value.Length == 0 ? "[dim]Unknown[/]" : scope.Value.EscapeMarkup(),
    };
}
