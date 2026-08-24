using FluentAssertions;
using Hall9k.Daemon.ProjectHomes;
using Xunit;

namespace Hall9k.Tests.Daemon.ProjectHomes;

/// <summary>
/// The daemon-start reconciliation pass's other half (backlog 48): a directory whose exact name
/// names nothing the store still knows about. Empty shells are removed outright; anything holding
/// real material is marked, never deleted.
/// </summary>
public sealed class HomeEntryReconcilerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hall9k-reconcile-").FullName;

    [Fact]
    public void A_directory_matching_a_known_name_is_left_untouched()
    {
        string directory = Path.Combine(_root, "aaaaaaaa-known-task");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "task.md"), "content");

        IReadOnlyList<string> handled = HomeEntryReconciler.RemoveOrMarkOrphans(_root, new HashSet<string> { "aaaaaaaa-known-task" }, "task.md");

        handled.Should().BeEmpty();
        Directory.Exists(directory).Should().BeTrue();
    }

    [Fact]
    public void A_stale_duplicate_left_by_an_interrupted_rename_is_reconciled_even_though_its_id_is_known()
    {
        // HomeEntryWriter leaves the old-named directory standing when a directory already sits at
        // both the old and the new name (HomeEntryWriterTests documents that collision case). A
        // prefix-only match would treat the stale "old-objective" directory as live because it
        // shares the "eeeeeeee" short id with the directory that is actually current; matching on
        // the whole name is what tells them apart.
        string staleDirectory = Path.Combine(_root, "eeeeeeee-old-objective");
        Directory.CreateDirectory(Path.Combine(staleDirectory, "workspace"));
        File.WriteAllText(Path.Combine(staleDirectory, "task.md"), "stale generated content");
        string currentDirectory = Path.Combine(_root, "eeeeeeee-new-objective");
        Directory.CreateDirectory(currentDirectory);
        File.WriteAllText(Path.Combine(currentDirectory, "task.md"), "current generated content");

        IReadOnlyList<string> handled = HomeEntryReconciler.RemoveOrMarkOrphans(
            _root, new HashSet<string> { "eeeeeeee-new-objective" }, "task.md");

        handled.Should().ContainSingle().Which.Should().Be(staleDirectory);
        Directory.Exists(staleDirectory).Should().BeFalse("an empty stale duplicate is reconciled away, not left invisible");
        Directory.Exists(currentDirectory).Should().BeTrue();
    }

    [Fact]
    public void A_directory_whose_short_id_failed_to_render_this_sweep_is_left_alone_even_though_it_matches_no_known_name()
    {
        // A failed Directory.Move (e.g. a transient IOException) can leave a live entry's directory
        // standing under its old name while the caller's "known names" set only knows the new one —
        // ProjectHomeRenderEngine passes the failed entity's short id here so this same sweep does
        // not mistake its own failure for an orphan.
        string directory = Path.Combine(_root, "ffffffff-old-objective");
        Directory.CreateDirectory(Path.Combine(directory, "workspace"));
        File.WriteAllText(Path.Combine(directory, "task.md"), "content that would otherwise look orphaned");

        IReadOnlyList<string> handled = HomeEntryReconciler.RemoveOrMarkOrphans(
            _root, new HashSet<string> { "ffffffff-renamed-objective" }, "task.md",
            new HashSet<string> { "ffffffff" });

        handled.Should().BeEmpty();
        Directory.Exists(directory).Should().BeTrue("the render that failed this sweep is still live; it must not be judged an orphan by the same sweep that failed it");
        File.Exists(Path.Combine(directory, "ORPHANED.md")).Should().BeFalse();
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
