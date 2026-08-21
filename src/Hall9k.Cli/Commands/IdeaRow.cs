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
    Guid? PromotedTaskId,
    DateTimeOffset CapturedAt)
{
    public static IdeaRow Compose(IdeaDetails idea, IReadOnlyDictionary<Guid, ProjectDetails> projects) =>
        new(idea.Id,
            idea.Text,
            idea.ProjectId,
            idea.ProjectId is { } projectId && projects.TryGetValue(projectId, out ProjectDetails? project)
                ? project.Name
                : null,
            idea.State,
            idea.PromotedTaskId,
            idea.CapturedAt);

    public string IdMarkup => $"[dim]{TaskListCommand.ShortId(Id)}[/]";

    public string StateMarkup => State.Value switch
    {
        "Captured" => "[blue]Captured[/]",
        "Promoted" => "[green]Promoted[/]",
        "Discarded" => "[dim]Discarded[/]",
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

    /// <summary>The note on one line, truncated to the width the fixed columns leave it.</summary>
    public string TextMarkup(int width) =>
        TaskListCommand.Truncate(Text.ReplaceLineEndings(" ").Trim(), width).EscapeMarkup();

    public string AgeMarkup(DateTimeOffset now) => TaskStatusComposer.RelativeAge(now - CapturedAt);
}
