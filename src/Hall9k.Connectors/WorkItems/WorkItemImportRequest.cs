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
/// <paramref name="WorkingDirectory"/> is the project's repository path. It reads as
/// filesystem plumbing but it is the source-agnostic half of "which account am I": every
/// provider Hall9k reaches for is a command-line tool that already holds the machine's
/// credentials (PLAN.md §10), and the directory it runs in is what tells <c>gh</c> which
/// repository the project means without the platform restating it.
/// </para>
/// </summary>
public sealed record WorkItemImportRequest(
    WorkItemProvider Provider,
    string Reference,
    string WorkingDirectory);
