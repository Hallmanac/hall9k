using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One import, asked in the shape every source answers: which system, the reference exactly as
/// the human typed it, and the directory the project lives in.
/// <para>
/// <paramref name="Reference"/> stays raw on purpose. Each source knows its own accepted forms
/// (a bare GitHub number, an <c>owner/repo#42</c> shorthand, a browser URL; a Jira key later),
/// and normalising here would mean the seam holding one source's grammar.
/// </para>
/// <para>
/// <paramref name="WorkingDirectory"/> is the project's repository path, and it is there for the
/// providers that answer "which account am I" from the filesystem: <c>gh</c> holds the machine's
/// own login (PLAN.md §10) and reads which repository the project means from the directory it
/// runs in, so passing the path is what saves the platform from restating it. A source with a
/// registered connection ignores it, because a Jira site and token say who is asking and a
/// directory says nothing — which is the asymmetry between the two connectors in one field.
/// </para>
/// </summary>
public sealed record WorkItemImportRequest(
    WorkItemProvider Provider,
    string Reference,
    string WorkingDirectory);
