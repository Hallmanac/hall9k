using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The adapter PLAN.md §9.2 names: one implementation per external system, so adding a source
/// adds a class and a line in <see cref="WorkItemImporter"/> rather than a second import path.
/// v0 implements the adopt half of the funnel (§3.1a) — read an existing item and link a task
/// to it. Create, transition, and comment join the interface when a feature needs them.
/// <para>
/// Note the two names: <see cref="WorkItemProvider"/> (the value object) says <em>which</em>
/// system an item belongs to and is persisted on events; <c>IWorkItemProvider</c> is the code
/// that talks to that system. The <see cref="Provider"/> property is the join between them.
/// </para>
/// <para>
/// An implementation reports what it observed and refuses what it could not: policy about
/// which items Hall9k will adopt lives in <see cref="WorkItemImporter"/>, so every source
/// inherits it rather than re-deciding it. Reporting faithfully includes mapping the system's
/// own status vocabulary onto <see cref="WorkItemStatus"/>: the importer adopts only an item
/// positively reported open, so a Jira "In Progress" that reaches it untranslated is refused
/// rather than assumed, and the adapter is the only layer that knows enough to translate.
/// </para>
/// </summary>
public interface IWorkItemProvider
{
    /// <summary>The system this adapter speaks for, and the key the importer selects it by.</summary>
    WorkItemProvider Provider { get; }

    /// <summary>
    /// Fetch one item. Throws a <see cref="Domain.Shared.Exceptions.DomainException"/> whose
    /// message names the concrete next move when the reference is unparseable, the item is
    /// missing, the tool is absent, or the credentials are not there — a caller (human or
    /// agent) has to be able to self-correct from the text alone (AGENTS.md, CLI standards).
    /// </summary>
    Task<ImportedWorkItem> ImportAsync(WorkItemImportRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Where a human clicks to see this reference, or null when this adapter cannot say. Kept
    /// on the seam rather than derived at the call site because "what does github:owner/repo#42
    /// point at" is provider knowledge, and the two surfaces that need it — <c>h9k task show</c>
    /// and the pull-request body — must not each own a copy of the rule.
    /// </summary>
    Uri? WebUrl(ExternalReference reference);
}
