using FluentAssertions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The discovery workspace is a directory under the platform home, derived from the idea's id
/// exactly as a run's directory is (Decisions Log #35) — so nothing about what accumulates in
/// it has to be recorded on the stream.
/// </summary>
// Redirects the process-wide HALL9K_HOME, so it shares the collection with the other tests
// that do: serialized, never yanking a home out from under a running one.
[Collection("Hall9kHome")]
public sealed class IdeaWorkspaceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"hall9k-ideas-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    public IdeaWorkspaceTests() => Environment.SetEnvironmentVariable("HALL9K_HOME", _home);

    [Fact]
    public void The_workspace_lives_under_the_platform_home_beside_the_runs()
    {
        Guid ideaId = DomainId.New();

        IdeaPaths.WorkspaceDirectory(ideaId).Should().Be(
            Path.Combine(_home, "ideas", ideaId.ToString(), "workspace"));
        PlatformPaths.Home.Should().Be(RunPaths.Root, "one home, one layout");
    }

    [Fact]
    public void Capture_makes_the_workspace_real_and_reading_it_counts_what_is_there()
    {
        Guid ideaId = DomainId.New();

        IdeaPaths.FileCount(ideaId).Should().BeNull("nothing has created it yet");

        string workspace = IdeaPaths.EnsureWorkspace(ideaId);
        Directory.Exists(workspace).Should().BeTrue();
        IdeaPaths.FileCount(ideaId).Should().Be(0, "an empty workspace is an invitation, not a problem");

        File.WriteAllText(Path.Combine(workspace, "notes.md"), "what is this?");
        Directory.CreateDirectory(Path.Combine(workspace, "prototype"));
        File.WriteAllText(Path.Combine(workspace, "prototype", "sketch.cs"), "// spike");

        IdeaPaths.FileCount(ideaId).Should().Be(2, "the count is taken when someone looks, nested files and all");
    }

    [Fact]
    public void Ensuring_a_workspace_twice_is_the_same_workspace()
    {
        Guid ideaId = DomainId.New();

        IdeaPaths.EnsureWorkspace(ideaId).Should().Be(IdeaPaths.EnsureWorkspace(ideaId));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }
}
