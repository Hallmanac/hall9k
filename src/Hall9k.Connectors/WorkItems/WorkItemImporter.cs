using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// The one door into the resolver seam: it picks the <see cref="IWorkItemProvider"/> for the
/// requested system and then applies the policy that is the platform's rather than any one
/// source's. Jira arrived as a provider in this list rather than as a second import path
/// (backlog 18), which is the point of routing every import through here even while there was
/// only one source.
/// <para>
/// There is no default instance, and the absence is the shape of the seam rather than an
/// oversight: GitHub piggybacks the machine's own <c>gh</c> login and can be constructed from
/// nothing, while Jira needs a site, an account, and a credential reference that only the
/// connection list knows (PLAN.md §10). One of those can have a static default and the other
/// cannot, so callers build an importer out of the registered connections
/// (<see cref="WorkItemConnections"/>), and a surface that could once place a reference with no
/// configuration is now honest about needing some.
/// </para>
/// <para>
/// Adoption policy lives here for the same reason: "Hall9k adopts open work" is a statement
/// about the funnel (PLAN.md §3.1a), not about GitHub, so a source that forgot to enforce it
/// cannot exist. That policy is read positively — only an item a source <em>called</em> open is
/// adopted — which makes translating a source's own vocabulary into
/// <see cref="WorkItemStatus"/> a provider's job rather than a gap the gate has to guess across.
/// </para>
/// </summary>
public sealed class WorkItemImporter(params IWorkItemProvider[] providers)
{
    /// <summary>
    /// Sources Hall9k speaks but this install has not registered, each mapped to the refusal that
    /// says how to register one. It exists because "no importer for jira" and "you have not
    /// connected Jira yet" are different sentences to the person reading them, and the second one
    /// is the true one on a first run: an install with no Jira connection builds a GitHub-only
    /// importer (<see cref="WorkItemConnections.ImporterAsync"/>), and without this the likely
    /// first command anybody types, h9k task add --from-jira, would answer with a list of known
    /// sources and no way forward. Origin incident (2026-08-21): the pre-PR review of the Jira
    /// branch found exactly that message on the unconnected path, while both sibling commands
    /// named h9k connection add jira.
    /// </summary>
    public IReadOnlyDictionary<WorkItemProvider, string> Unregistered { get; init; } =
        new Dictionary<WorkItemProvider, string>();

    public async Task<ImportedWorkItem> ImportAsync(
        WorkItemImportRequest request, CancellationToken cancellationToken)
    {
        IWorkItemProvider provider = Find(request.Provider) ?? throw Unavailable(request.Provider);

        ImportedWorkItem item = await provider.ImportAsync(request, cancellationToken);

        // The gate is positively open rather than not-closed. A closed item is finished work and
        // a task seeded from one carries a contract nobody is waiting on; but a state Hall9k
        // never read, or read and had no rule for, is not evidence of open work either, and
        // adopting it would be the never-guess rule broken at the funnel's own front door
        // (AGENTS.md). The observed status is quoted verbatim with its timestamp because that is
        // all the platform knows: it does not watch the item afterwards, so this says what was
        // true when we looked and nothing about now.
        //
        // The status and the reference are quoted through the one-line rule on the way into the
        // message rather than raw. Both are the source's own words — WorkItemStatus keeps a state
        // it had no rule for exactly as it was reported — and this message is printed to a
        // terminal, where an escape sequence in one of them would repaint the refusal that is
        // quoting it. Quoting verbatim means the words, not the characters that act.
        return item.Status.IsOpen
            ? item
            : throw new DomainValidationException(
                $"{RelayedText.OneLine(item.Reference.ToString())} was not open when Hall9k read it: "
                + $"the source said '{RelayedText.OneLine(item.Status.ToString())}' "
                + $"({item.ObservedStamp}), and adoption is for "
                + "work a source positively calls open. Reopen it and import again, or write the "
                + "task directly: h9k task add --project <name> --objective \"…\"");
    }

    /// <summary>
    /// Where a reference points, asked of whichever source owns it; null when no registered
    /// source recognises the provider, which is an honest "we cannot say" rather than a URL
    /// shaped like the one we would have guessed.
    /// </summary>
    public Uri? WebUrl(ExternalReference? reference) =>
        reference is null ? null : Find(reference.Provider)?.WebUrl(reference);

    /// <summary>The same question asked of the canonical string a projection stores.</summary>
    public Uri? WebUrl(string? canonicalReference) =>
        canonicalReference.IsBlank() ? null : WebUrl(ExternalReference.Parse(canonicalReference));

    private IWorkItemProvider? Find(WorkItemProvider provider) =>
        providers.FirstOrDefault(candidate => candidate.Provider == provider);

    /// <summary>
    /// Why this source is not here: unregistered on this install, which is a thing the human can
    /// fix and the message says how, or genuinely unknown, which is a typo or a source Hall9k does
    /// not speak.
    /// </summary>
    private Exception Unavailable(WorkItemProvider provider) =>
        Unregistered.TryGetValue(provider, out string? remedy)
            ? new DomainNotFoundException(remedy)
            : new DomainValidationException(
                $"Hall9k has no importer for '{RelayedText.OneLine(provider.Value)}'. Known sources: "
                + $"{string.Join(", ", providers.Select(p => p.Provider.Value))}.");
}
