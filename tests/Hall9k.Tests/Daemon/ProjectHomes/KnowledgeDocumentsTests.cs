using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Daemon.ProjectHomes;

/// <summary>
/// The filesystem half of the decisions and lessons render (idea d805fd8b, piece 2): write only
/// what changed, and never write over a file this platform did not render.
/// </summary>
public sealed class KnowledgeDocumentsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("h9k-knowledge-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private RenderedKnowledgeDocuments Rendered =>
        new(DecisionsDocumentRenderer.Render([]), LessonsDocumentRenderer.Render([]));

    [Fact]
    public void Both_documents_are_written_on_a_first_pass()
    {
        KnowledgeDocumentWriteResult result = KnowledgeDocuments.WriteInto(_directory, Rendered);

        result.Written.Should().Be(2);
        result.SkippedForeignFiles.Should().BeEmpty();
        File.ReadAllText(KnowledgeDocumentPaths.DecisionsFileIn(_directory))
            .Should().Contain(DecisionsDocumentRenderer.GeneratedMarker);
        File.ReadAllText(KnowledgeDocumentPaths.LessonsFileIn(_directory))
            .Should().Contain(LessonsDocumentRenderer.GeneratedMarker);
    }

    /// <summary>
    /// A render is a pure function of the store, so a sweep that finds nothing changed must not
    /// touch disk — the same rule <see cref="HomeEntryWriter"/> holds for a task or an idea.
    /// </summary>
    [Fact]
    public void A_second_pass_over_unchanged_records_writes_nothing()
    {
        KnowledgeDocuments.WriteInto(_directory, Rendered);

        KnowledgeDocuments.WriteInto(_directory, Rendered).Written.Should().Be(0);
    }

    [Fact]
    public void An_earlier_render_is_replaced_when_the_records_change()
    {
        KnowledgeDocuments.WriteInto(_directory, Rendered);
        DecisionDetails decision = new()
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000ff"),
            Statement = "Something has since been decided.",
            Status = DecisionStatus.Recorded,
            RecordedAt = DateTimeOffset.UnixEpoch,
        };

        KnowledgeDocumentWriteResult result = KnowledgeDocuments.WriteInto(
            _directory,
            new RenderedKnowledgeDocuments(
                DecisionsDocumentRenderer.Render([decision]), LessonsDocumentRenderer.Render([])));

        result.Written.Should().Be(1, "only the decisions document's bytes actually changed");
        File.ReadAllText(KnowledgeDocumentPaths.DecisionsFileIn(_directory))
            .Should().Contain("Something has since been decided.");
    }

    /// <summary>
    /// A worktree is somebody's repository. A project that genuinely tracks a root
    /// <c>decisions.md</c> would otherwise have it overwritten on every dispatch and turn up as a
    /// modified tracked file in the session's own diff, so the file is left exactly as it is and
    /// the caller is told which one it was.
    /// </summary>
    [Fact]
    public void A_file_this_platform_did_not_render_is_left_alone_and_named()
    {
        string foreign = KnowledgeDocumentPaths.DecisionsFileIn(_directory);
        File.WriteAllText(foreign, "# Decisions\n\nThis project keeps its own, by hand.\n");

        KnowledgeDocumentWriteResult result = KnowledgeDocuments.WriteInto(_directory, Rendered);

        result.Written.Should().Be(1, "lessons.md was free to write; decisions.md was not");
        result.SkippedForeignFiles.Should().ContainSingle().Which.Should().Be(foreign);
        File.ReadAllText(foreign).Should().Be("# Decisions\n\nThis project keeps its own, by hand.\n");
    }

    /// <summary>
    /// Both names reach the repository's own exclude list, and a second dispatch into the same
    /// clone adds nothing — every worktree of one clone shares that file, so "already there" is
    /// the common case rather than the exception.
    /// </summary>
    [Fact]
    public async Task Ensuring_the_ignore_puts_both_names_on_the_repositorys_exclude_list_once()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".git"));

        (await KnowledgeDocuments.EnsureIgnoredAsync(_directory, CancellationToken.None)).Should().BeTrue();
        (await KnowledgeDocuments.EnsureIgnoredAsync(_directory, CancellationToken.None)).Should().BeTrue();

        string exclude = await File.ReadAllTextAsync(Path.Combine(_directory, ".git", "info", "exclude"));
        exclude.Should().Contain("/decisions.md").And.Contain("/lessons.md");
        exclude.Split(WorktreeExcludeFile.BlockHeading).Should().HaveCount(2, "the block is written once, not per dispatch");
    }

    [Fact]
    public async Task A_directory_whose_repository_cannot_be_found_reports_that_rather_than_writing_anywhere()
    {
        (await KnowledgeDocuments.EnsureIgnoredAsync(_directory, CancellationToken.None)).Should().BeFalse();
    }
}
