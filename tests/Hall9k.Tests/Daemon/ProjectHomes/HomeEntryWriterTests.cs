using FluentAssertions;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon.ProjectHomes;

/// <summary>
/// The filesystem half of a task or idea render (backlog 48): create, ensure workspace/, write
/// only when the content actually changed, and move the directory when the slug changed instead
/// of leaving a stale copy behind.
/// </summary>
public sealed class HomeEntryWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hall9k-home-entry-").FullName;
    private readonly Guid _id = DomainId.New();
    private readonly string _shortId;

    public HomeEntryWriterTests() => _shortId = DomainId.Short(_id);

    [Fact]
    public void First_write_creates_the_directory_the_file_and_an_empty_workspace()
    {
        string directoryName = $"{_shortId}-first-cut";
        HomeEntryWriteResult result = HomeEntryWriter.Write(_root, _id, directoryName, "task.md", "hello");

        result.Changed.Should().BeTrue();
        result.DirectoryPath.Should().Be(Path.Combine(_root, directoryName));
        File.ReadAllText(Path.Combine(result.DirectoryPath, "task.md")).Should().Be("hello");
        Directory.Exists(Path.Combine(result.DirectoryPath, "workspace")).Should().BeTrue();
    }

    [Fact]
    public void Rewriting_identical_content_reports_unchanged_and_touches_nothing()
    {
        string directoryName = $"{_shortId}-first-cut";
        HomeEntryWriter.Write(_root, _id, directoryName, "task.md", "hello");
        string filePath = Path.Combine(_root, directoryName, "task.md");
        DateTime before = File.GetLastWriteTimeUtc(filePath);

        HomeEntryWriteResult second = HomeEntryWriter.Write(_root, _id, directoryName, "task.md", "hello");

        second.Changed.Should().BeFalse();
        File.GetLastWriteTimeUtc(filePath).Should().Be(before);
    }

    [Fact]
    public void Changed_content_is_written_in_place()
    {
        string directoryName = $"{_shortId}-first-cut";
        HomeEntryWriter.Write(_root, _id, directoryName, "task.md", "hello");

        HomeEntryWriteResult second = HomeEntryWriter.Write(_root, _id, directoryName, "task.md", "goodbye");

        second.Changed.Should().BeTrue();
        File.ReadAllText(Path.Combine(second.DirectoryPath, "task.md")).Should().Be("goodbye");
    }

    [Fact]
    public void A_slug_change_moves_the_existing_directory_rather_than_leaving_a_stale_copy()
    {
        string oldName = $"{_shortId}-old-objective";
        string newName = $"{_shortId}-new-objective";
        HomeEntryWriter.Write(_root, _id, oldName, "task.md", "hello");
        File.WriteAllText(Path.Combine(_root, oldName, "workspace", "notes.md"), "keep me");

        HomeEntryWriteResult second = HomeEntryWriter.Write(_root, _id, newName, "task.md", "hello v2");

        Directory.Exists(Path.Combine(_root, oldName)).Should().BeFalse();
        second.DirectoryPath.Should().Be(Path.Combine(_root, newName));
        File.ReadAllText(Path.Combine(second.DirectoryPath, "task.md")).Should().Be("hello v2");
        File.ReadAllText(Path.Combine(second.DirectoryPath, "workspace", "notes.md")).Should().Be("keep me",
            "the workspace is where refinement material accumulates; a rename must carry it along");
    }

    [Fact]
    public void A_directory_already_at_both_the_old_and_new_name_uses_the_target_and_leaves_the_old_one_untouched()
    {
        string oldName = $"{_shortId}-old-objective";
        string newName = $"{_shortId}-new-objective";
        HomeEntryWriter.Write(_root, _id, oldName, "task.md", "hello");
        Directory.CreateDirectory(Path.Combine(_root, newName));

        HomeEntryWriteResult result = HomeEntryWriter.Write(_root, _id, newName, "task.md", "hello v2");

        result.DirectoryPath.Should().Be(Path.Combine(_root, newName),
            "the correctly-named directory wins outright, so which of two same-prefix directories "
            + "is 'the' existing one is never ambiguous");
        Directory.Exists(Path.Combine(_root, oldName)).Should().BeTrue(
            "left alone rather than silently merged or deleted");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
