using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The rendering the four decision and lesson surfaces share (idea d805fd8b, piece 1): the
/// provenance block both <c>show</c> commands print, and the scope cell and project-name lookup
/// both <c>list</c> commands build their rows from. Shared because each piece is identical for a
/// decision and for a lesson and reads the same way whichever surface printed it, and because
/// the one thing it must never do — quietly present an absence as a value — is easier to keep
/// true in one place than in four.
/// </summary>
internal static class KnowledgeRecordRendering
{
    /// <summary>
    /// Writes the who, the where-from, and the attendance. A null run or task prints "none
    /// recorded", which is what the stream actually says: nothing was observed, rather than
    /// nothing happened.
    /// </summary>
    public static void Write(RecordedProvenance? provenance, string ownerLabel)
    {
        if (provenance is null)
        {
            AnsiConsole.MarkupLine("[dim]Provenance[/]   none recorded on this stream");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Recorded by[/]  {ownerLabel.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[dim]From run[/]     {Reference(provenance.RunId)}");
        AnsiConsole.MarkupLine($"[dim]From task[/]    {Reference(provenance.TaskId)}");
        AnsiConsole.MarkupLine($"[dim]Attendance[/]   {Attendance(provenance.Attendance)}");
    }

    /// <summary>The owner's own name where this install knows it, and the bare id where it does not.</summary>
    public static async Task<string> OwnerLabelAsync(
        IQuerySession session, Guid ownerId, CancellationToken cancellationToken)
    {
        Domain.Features.Owner.OwnerDetails? owner =
            await session.LoadAsync<Domain.Features.Owner.OwnerDetails>(ownerId, cancellationToken);
        return owner?.Name.IsNotBlank() == true
            ? $"{owner.Name} ({DomainId.Short(ownerId)})"
            : ownerId.ToString();
    }

    /// <summary>The project's own name where this install knows it, and the bare id where it does not.</summary>
    public static async Task<string> ProjectLabelAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken)
    {
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        return project?.Name ?? projectId.ToString();
    }

    /// <summary>
    /// The project names behind a page of list rows, in one round trip. Shared by both list
    /// commands, which ask the identical question of two different row types.
    /// </summary>
    public static async Task<Dictionary<Guid, string>> ProjectNamesAsync(
        IQuerySession session,
        IEnumerable<(KnowledgeScope Scope, Guid ScopeId)> rows,
        CancellationToken cancellationToken)
    {
        Guid[] projectIds = [.. rows
            .Where(row => row.Scope == KnowledgeScope.Project)
            .Select(row => row.ScopeId)
            .Distinct()];

        return projectIds.Length == 0
            ? []
            : (await session.LoadManyAsync<ProjectDetails>(cancellationToken, projectIds))
                .ToDictionary(project => project.Id, project => project.Name);
    }

    /// <summary>
    /// One list row's scope cell: the project's name where this node holds it, the bare short id
    /// where it does not, and the word "owner" for a cross-project record. Markup-escaped, since
    /// a project name is whatever a human typed.
    /// </summary>
    public static string ScopeCell(
        KnowledgeScope scope, Guid scopeId, IReadOnlyDictionary<Guid, string> projectNames) =>
        scope == KnowledgeScope.Owner
            ? "[dim]owner[/]"
            : projectNames.TryGetValue(scopeId, out string? name)
                ? name.EscapeMarkup()
                : $"[dim]{DomainId.Short(scopeId)}[/]";

    private static string Reference(Guid? id) =>
        id is { } value ? DomainId.Short(value) : "[dim]none recorded[/]";

    private static string Attendance(HumanAttendance attendance) =>
        attendance == HumanAttendance.Attended ? "a human was attending the run"
        : attendance == HumanAttendance.Unattended ? "unattended — a dispatched agent's own run"
        : attendance == HumanAttendance.Unobserved ? "[dim]not observed either way[/]"
        : "[dim]unrecorded[/]";
}
