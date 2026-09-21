using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project.Projections;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One idea as the browse table reads it. Composed apart from the query so the layout can be
/// rendered and measured without a database, exactly as <see cref="TaskStatusRow"/> is.
/// </summary>
internal sealed record IdeaRow(
    Guid Id,
    string Text,
    Guid? ProjectId,
    string? ProjectName,
    IdeaState State,
    DateTimeOffset CapturedAt,
    /// <summary>
    /// Whether this row's own <c>IdeaDetails</c> is headless: <see cref="State"/> is
    /// <see cref="IdeaState.Unknown"/> or <see cref="CapturedAt"/> is <c>default</c> — the shape
    /// Marten leaves behind when a replicated tail event auto-vivified a document with no matching
    /// <c>IdeaCaptured</c> ever applied (an idea whose genesis predates the sender's outbox). Never
    /// true for an idea this install actually captured: <c>IdeaAddCommand</c> always supplies a
    /// real timestamp in the same transaction that starts the stream, and <c>Create</c> always sets
    /// <see cref="IdeaState.Captured"/>.
    /// </summary>
    bool PartialHistoryHeld = false)
{
    public static IdeaRow Compose(IdeaDetails idea, IReadOnlyDictionary<Guid, ProjectDetails> projects) =>
        new(idea.Id,
            idea.Text,
            idea.ProjectId,
            idea.ProjectId is { } projectId && projects.TryGetValue(projectId, out ProjectDetails? project)
                ? project.Name
                : null,
            idea.State,
            idea.CapturedAt,
            IsPartialHistoryHeld(idea));

    /// <summary>See <see cref="PartialHistoryHeld"/>'s own doc for why either tell alone is authoritative.</summary>
    internal static bool IsPartialHistoryHeld(IdeaDetails idea) =>
        idea.State == IdeaState.Unknown || idea.CapturedAt == default;

    public string IdMarkup => $"[dim]{TaskListCommand.ShortId(Id)}[/]";

    public string StateMarkup => State.Value switch
    {
        "Captured" => "[blue]Captured[/]",
        "Concluded" => "[green]Concluded[/]",
        "Archived" => "[dim]Archived[/]",
        _ => State.Value.EscapeMarkup(),
    };

    /// <summary>
    /// An idea with no project says so rather than showing an empty cell: the absence is a
    /// fact about the idea (it may precede its project, or become one), not missing data.
    /// </summary>
    public string ProjectMarkup => ProjectName is not null
        ? ProjectName.EscapeMarkup()
        : ProjectId is null
            ? "[dim]none[/]"
            : $"[dim]{TaskListCommand.ShortId(ProjectId.Value)}[/]";

    /// <summary>
    /// The note on one line, truncated to the width the fixed columns leave it — or, for a
    /// headless row shown only because <c>--all</c> asked for it back, what it actually is rather
    /// than the blank note Marten's auto-vivified document carries.
    /// </summary>
    public string TextMarkup(int width) =>
        PartialHistoryHeld
            ? "[red]partial history held[/] — its own genesis event never arrived"
            : TaskListCommand.Truncate(Text.ReplaceLineEndings(" ").Trim(), width).EscapeMarkup();

    public string AgeMarkup(DateTimeOffset now) => TaskStatusComposer.RelativeAge(now - CapturedAt);
}
