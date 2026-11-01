using FluentAssertions;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// No surface a review session can reach prints a note. The prompts that carry notes and the verbs
/// that print them are a short, named list; this fails the build if any other source file starts
/// reading them, so a new reader has to be added here on purpose.
/// </summary>
public sealed class TaskNoteSurfaceGuardTests
{
    private static readonly string[] Readers =
    [
        "src/Hall9k.Domain/Features/Tasks/Notes/",
        "src/Hall9k.Domain/Features/Tasks/Queries/TaskNoteFeed.cs",
        "src/Hall9k.Domain/Features/Tasks/Handlers/TaskNoteDecider.cs",
        "src/Hall9k.Domain/Features/Tasks/Events/TaskNoteRecorded.cs",
        "src/Hall9k.Domain/Features/Tasks/Projections/TaskDetails.cs",
        "src/Hall9k.Domain/Infrastructure/Persistence/EventScopeRegistry.cs",
        "src/Hall9k.Domain/Infrastructure/Persistence/TaskActClassificationRegistry.cs",
        "src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs",
        "src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs",
        "src/Hall9k.Daemon/Execution/RunLauncher.cs",
        "src/Hall9k.Daemon/Execution/TaskNoteCapture.cs",
        "src/Hall9k.Daemon/Review/ReviewEngine.cs",
        "src/Hall9k.Daemon/ProjectHomes/ProjectHomeRenderEngine.cs",
        "src/Hall9k.Daemon/ProjectHomes/TaskNotesDocument.cs",
        "src/Hall9k.Cli/Commands/TaskNoteCommand.cs",
        "src/Hall9k.Cli/Commands/TaskNotesCommand.cs",
        "src/Hall9k.Cli/Commands/TaskDriveCommand.cs",
        "src/Hall9k.Cli/Commands/TaskStartCommand.cs",
        "src/Hall9k.Cli/Commands/TaskDelegateCommand.cs",
        "src/Hall9k.Cli/Infrastructure/DispatchedSessionCommandClassification.cs",
    ];

    [Fact]
    public void Only_the_named_files_read_a_tasks_notes()
    {
        string root = RepositoryRoot();
        string[] needles = ["TaskNoteFeed", "InjectedTaskNotes", "TaskNoteEntry", "TaskNoteView", ".Notes."];
        List<string> readers = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            bool mentions = needles.Take(4).Any(needle => text.Contains(needle, StringComparison.Ordinal))
                || (text.Contains("TaskDetails", StringComparison.Ordinal) && text.Contains(".Notes.", StringComparison.Ordinal));
            if (mentions && !Readers.Any(allowed => relative.StartsWith(allowed, StringComparison.Ordinal)))
            {
                readers.Add(relative);
            }
        }

        readers.Should().BeEmpty(
            "task show, task next, task list, status, epic show, the feed, the courier and the review prompt builders "
            + "must not read a note; add a file here only when it is a deliberate reader");
    }

    [Fact]
    public void The_rendered_task_document_contains_no_note_text()
    {
        TaskDetails task = new()
        {
            Id = DomainId.New(),
            Objective = "Bound the limiter",
            AcceptanceCriteria = ["it resets"],
        };
        task.Notes.Add(new TaskNoteEntry(
            new TaskNoteRecorded(
                task.Id, DomainId.New(), "SECRET-NOTE-TEXT", RecordedProvenance.FromShell(DomainId.New()),
                TaskNoteSource.Verb, DateTimeOffset.UtcNow),
            DomainId.New(), null, null));

        TaskDocumentRenderer.Render(task, "hall9k", epicJiraReference: null).Should().NotContain("SECRET-NOTE-TEXT");
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Hall9k.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
