using System.Globalization;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One external work item as a single source reported it at a single moment: the shared shape
/// behind the resolver seam, so a second source is a new <see cref="IWorkItemProvider"/> rather
/// than a second import path.
/// <para>
/// Everything here is an observation, which is why <see cref="ObservedAt"/> travels with it.
/// Nothing on this record is refreshed later and nothing is inferred: an item with no body
/// carries a null body rather than a summary of its title, and <see cref="Status"/> is what the
/// source said when asked, not what the item is now (AGENTS.md, never guess at unobserved facts).
/// </para>
/// <para>
/// <see cref="Title"/> is a seed for the task's objective and never the objective itself: the
/// human confirms or replaces it. <see cref="Body"/> becomes agent context and never acceptance
/// criteria, which is the whole design constraint of the import (PLAN.md §4 — criteria are the
/// readiness contract, and a contract nobody agreed to is not one).
/// </para>
/// </summary>
public sealed record ImportedWorkItem(
    ExternalReference Reference,
    string Title,
    string? Body,
    WorkItemStatus Status,
    Uri? Url,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// <see cref="ObservedAt"/> written the one way every reader of it will see. It is formatted
    /// here rather than at each place that prints it, and with the invariant culture, because the
    /// stamp is not a display detail: it is copied into the task's stored agent context and into
    /// the refusal an event stream keeps, where it outlives the machine that wrote it. Formatted
    /// with the current culture instead, an import run on a Finnish or Danish machine would record
    /// '09.30.00Z' permanently, and the same import would read differently depending on who ran it.
    /// <para>
    /// The trailing Z is true because the moment is converted to UTC first rather than assumed to
    /// already be there — a stamp that names a zone it was not measured in is the never-guess rule
    /// (AGENTS.md) broken in the smallest possible way.
    /// </para>
    /// </summary>
    public string ObservedStamp =>
        ObservedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}
