using System.Globalization;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// How <c>h9k orchestrator feed</c> lays its items out (idea 89471598, piece 2): oldest first,
/// grouped by task, one plain line each. Plain text, never Spectre markup — an item quotes
/// whatever a person or an agent wrote, and a body that happens to contain square brackets must
/// reach the terminal as itself rather than as a colour tag.
/// <para>
/// Both halves live here rather than in the command so a unit test can pin the whole shape as a
/// golden without a database or a console: <see cref="Group"/> decides the order and the
/// grouping, <see cref="Render"/> decides the characters.
/// </para>
/// </summary>
public static class OrchestratorFeedRenderer
{
    /// <summary>The heading for everything that belongs to no task at all — an idea, a project
    /// setting, a message about nothing in particular.</summary>
    public const string UnattachedHeading = "Not about one task";

    /// <summary>
    /// Items grouped by task, each group's items oldest first, and the groups themselves ordered
    /// by their own oldest item — so reading the whole output top to bottom is still reading the
    /// project's history in order, rather than jumping backwards at every heading.
    /// </summary>
    /// <param name="headingForTask">
    /// How a task is named. The caller owns this because naming a task means reading its
    /// objective, which this layer cannot do — and a heading that fell back to the bare id is
    /// exactly what the feed is not allowed to print.
    /// </param>
    public static IReadOnlyList<OrchestratorFeedGroup> Group(
        IEnumerable<OrchestratorFeedItem> items,
        Func<Guid, string> headingForTask) =>
    [
        .. items
            .GroupBy(item => item.TaskId)
            .Select(group => new OrchestratorFeedGroup(
                group.Key,
                group.Key is { } taskId ? headingForTask(taskId) : UnattachedHeading,
                [.. group.OrderBy(item => item.Sequence)]))
            .OrderBy(group => group.Items[0].Sequence),
    ];

    /// <summary>
    /// The printed lines, in order: a heading, then one indented line per item carrying its own
    /// timestamp, in a fixed culture-invariant shape so two machines reading the same event print
    /// the same characters.
    /// </summary>
    /// <param name="zone">
    /// Which clock the timestamps are written in. Required rather than defaulted: an event is
    /// recorded in UTC and the rest of this platform's own surfaces print local time
    /// (<c>h9kd.log</c>, <c>h9k status</c>), so a feed that quietly printed UTC beside them would
    /// have an operator reading two clocks as one. The CLI passes
    /// <see cref="TimeZoneInfo.Local"/>; a golden test passes <see cref="TimeZoneInfo.Utc"/> so
    /// its expectation does not move with the machine it runs on.
    /// </param>
    public static IReadOnlyList<string> Render(IEnumerable<OrchestratorFeedGroup> groups, TimeZoneInfo zone) =>
    [
        .. groups.SelectMany(group => (string[])
        [
            group.Heading,
            .. group.Items.Select(item =>
                "  "
                + TimeZoneInfo.ConvertTime(item.At, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + $"  {item.Description}"),
        ]),
    ];
}
