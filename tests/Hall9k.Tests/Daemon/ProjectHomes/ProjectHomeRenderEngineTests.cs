using FluentAssertions;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon.ProjectHomes;

/// <summary>
/// The render sweep's own filter on which idea it is safe to touch a directory for: absent, or
/// rooted in this host's own path shape. A workspace home replicated from a node on a different
/// operating system — a Windows path applied on macOS, or the reverse — is a node-local fact about
/// a different machine, never a directory this host can create or read.
/// </summary>
public sealed class ProjectHomeRenderEngineTests
{
    [Fact]
    public void An_idea_with_no_recorded_workspace_home_is_renderable()
    {
        IdeaDetails idea = new() { Id = DomainId.New(), WorkspaceHome = ProjectHome.None };

        ProjectHomeRenderEngine.CanRenderIdea(idea).Should().BeTrue();
    }

    [Fact]
    public void An_idea_whose_workspace_home_is_rooted_in_this_hosts_own_shape_is_renderable()
    {
        string nativePath = OperatingSystem.IsWindows() ? @"C:\Users\bob\.hall9k\projects\hall9k" : "/Users/bob/.hall9k/projects/hall9k";
        IdeaDetails idea = new() { Id = DomainId.New(), WorkspaceHome = ProjectHome.Parse(nativePath) };

        ProjectHomeRenderEngine.CanRenderIdea(idea).Should().BeTrue();
    }

    /// <summary>
    /// Idea 202383dc: a node captures its own WorkspaceHomeDirectory in its own path shape, and
    /// that fact never resolves to a directory anywhere but that machine. The render sweep must
    /// skip such an idea entirely rather than let anything downstream misread a foreign path's own
    /// syntax as this host's.
    /// </summary>
    [Fact]
    public void An_idea_whose_workspace_home_is_rooted_in_the_other_operating_systems_shape_is_not_renderable()
    {
        string foreignPath = OperatingSystem.IsWindows() ? "/Users/bob/.hall9k/projects/hall9k" : @"C:\Users\bob\.hall9k\projects\hall9k";
        IdeaDetails idea = new() { Id = DomainId.New(), WorkspaceHome = ProjectHome.Parse(foreignPath) };

        ProjectHomeRenderEngine.CanRenderIdea(idea).Should().BeFalse();
    }
}
