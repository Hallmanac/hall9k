using FluentAssertions;
using Hall9k.Daemon.ProjectHomes;
using Xunit;

namespace Hall9k.Tests.Daemon.ProjectHomes;

/// <summary>
/// The daemon-start reconciliation pass's other half (backlog 48): a directory whose short-id
/// prefix names nothing the store still knows about. Empty shells are removed outright; anything
/// holding real material is marked, never deleted.
/// </summary>
public sealed class HomeEntryReconcilerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hall9k-reconcile-").FullName;

    [Fact]
    public void A_directory_matching_a_known_id_is_left_untouched()
    {
        string directory = Path.Combine(_root, "aaaaaaaa-known-task");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "task.md"), "content");

        IReadOnlyList<string> handled = HomeEntryReconciler.RemoveOrMarkOrphans(_root, new HashSet<string> { "aaaaaaaa" }, "task.md");

        handled.Should().BeEmpty();
        Directory.Exists(directory).Should().BeTrue();
    }

    [Fact]
    public void An_empty_shell_orphan_is_removed()
    {
        string directory = Path.Combine(_root, "bbbbbbbb-orphaned-task");
        Directory.CreateDirectory(Path.Combine(directory, "workspace"));
        File.WriteAllText(Path.Combine(directory, "task.md"), "generated content");

        IReadOnlyList<string> handled = HomeEntryReconciler.RemoveOrMarkOrphans(_root, new HashSet<string>(), "task.md");

        handled.Should().ContainSingle().Which.Should().Be(directory);
        Directory.Exists(directory).Should().BeFalse();
    }

    [Fact]
    public void An_orphan_holding_real_material_is_marked_never_deleted()
    {
        string directory = Path.Combine(_root, "cccccccc-orphaned-idea");
        Directory.CreateDirectory(Path.Combine(directory, "workspace"));
        File.WriteAllText(Path.Combine(directory, "idea.md"), "generated content");
        File.WriteAllText(Path.Combine(directory, "workspace", "prototype.cs"), "// real work");

        IReadOnlyList<string> handled = HomeEntryReconciler.RemoveOrMarkOrphans(_root, new HashSet<string>(), "idea.md");

        handled.Should().ContainSingle();
        Directory.Exists(directory).Should().BeTrue("a human put real material here; a sweep must never delete it");
        File.Exists(Path.Combine(directory, "workspace", "prototype.cs")).Should().BeTrue();
        File.Exists(Path.Combine(directory, "ORPHANED.md")).Should().BeTrue();
    }

    [Fact]
    public void Marking_twice_does_not_duplicate_or_overwrite_the_note()
    {
        string directory = Path.Combine(_root, "dddddddd-orphaned-idea");
        Directory.CreateDirectory(Path.Combine(directory, "workspace"));
        File.WriteAllText(Path.Combine(directory, "workspace", "notes.md"), "keep me");

        HomeEntryReconciler.RemoveOrMarkOrphans(_root, new HashSet<string>(), "idea.md");
        DateTime firstMarked = File.GetLastWriteTimeUtc(Path.Combine(directory, "ORPHANED.md"));
        HomeEntryReconciler.RemoveOrMarkOrphans(_root, new HashSet<string>(), "idea.md");

        File.GetLastWriteTimeUtc(Path.Combine(directory, "ORPHANED.md")).Should().Be(firstMarked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
