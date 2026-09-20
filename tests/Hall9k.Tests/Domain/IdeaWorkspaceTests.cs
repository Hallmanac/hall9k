using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The discovery workspace's global fallback is a directory under the platform home, derived
/// from the idea's id (Decisions Log #35); a home-resident one lives under the project's home
/// instead, at the directory the idea currently renders under (backlog 49) — see
/// <see cref="IdeaLifecycleTests"/> and <see cref="IdeaPaths"/> for the capture-time decision
/// between the two.
/// </summary>
// Redirects HALL9K_HOME through this class's own ScopedTestHome, never the process-wide
// variable itself.
public sealed class IdeaWorkspaceTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();

    private string _home => _scopedHome.Home;

    [Fact]
    public void The_global_directory_lives_under_the_platform_home_beside_the_runs()
    {
        Guid ideaId = DomainId.New();

        IdeaPaths.GlobalDirectory(ideaId).Should().Be(Path.Combine(_home, "ideas", ideaId.ToString()));
        PlatformPaths.Home.Should().Be(RunPaths.Root, "one home, one layout");
    }

    [Fact]
    public void ResolveDirectory_falls_back_to_global_with_no_recorded_home()
    {
        Guid ideaId = DomainId.New();

        IdeaPaths.ResolveDirectory(ProjectHome.None, "abc-some-idea", ideaId)
            .Should().Be(IdeaPaths.GlobalDirectory(ideaId));
    }

    [Fact]
    public void ResolveDirectory_uses_the_recorded_home_and_the_current_directory_name()
    {
        Guid ideaId = DomainId.New();
        ProjectHome home = ProjectHome.Parse(Path.Combine(_home, "projects", "demo"));

        IdeaPaths.ResolveDirectory(home, "abc12345-some-idea", ideaId).Should().Be(
            ProjectHomePaths.IdeaDirectory(home.Value, "abc12345-some-idea"),
            "the leaf is recomputed from the idea's current directory name, not frozen at capture");
    }

    [Fact]
    public void Capture_makes_the_workspace_real_and_reading_it_counts_what_is_there()
    {
        string ideaDirectory = IdeaPaths.GlobalDirectory(DomainId.New());

        IdeaPaths.FileCount(ideaDirectory).Should().BeNull("nothing has created it yet");

        string workspace = IdeaPaths.EnsureWorkspace(ideaDirectory);
        Directory.Exists(workspace).Should().BeTrue();
        IdeaPaths.FileCount(ideaDirectory).Should().Be(0, "an empty workspace is an invitation, not a problem");

        File.WriteAllText(Path.Combine(workspace, "notes.md"), "what is this?");
        Directory.CreateDirectory(Path.Combine(workspace, "prototype"));
        File.WriteAllText(Path.Combine(workspace, "prototype", "sketch.cs"), "// spike");

        IdeaPaths.FileCount(ideaDirectory).Should().Be(2, "the count is taken when someone looks, nested files and all");
    }

    [Fact]
    public void Ensuring_a_workspace_twice_is_the_same_workspace()
    {
        string ideaDirectory = IdeaPaths.GlobalDirectory(DomainId.New());

        IdeaPaths.EnsureWorkspace(ideaDirectory).Should().Be(IdeaPaths.EnsureWorkspace(ideaDirectory));
    }

    public void Dispose() => _scopedHome.Dispose();
}
