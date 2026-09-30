using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The seam every prompt builder actually reads a project's own addendum through: the daemon's
/// local materialized copy on disk, never a live ledger read (idea b9b09779, piece 6).
/// </summary>
public sealed class ProjectPromptAddendaLoaderTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"h9k-prompt-addenda-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void No_home_recorded_yet_reads_as_no_addendum()
    {
        ProjectDetails project = new() { HomeDirectory = ProjectHome.None };

        ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Work).Should().BeNull();
    }

    [Fact]
    public void No_materialized_file_yet_reads_as_no_addendum()
    {
        ProjectDetails project = ProjectWithHome();

        ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Work).Should().BeNull();
    }

    [Fact]
    public void An_ordinary_materialized_file_reads_as_its_own_content_and_not_over_cap()
    {
        ProjectDetails project = ProjectWithHome();
        WriteAddendum(PromptBuilderKey.Work, "Prefer squash commits.");

        LoadedPromptAddendum? loaded = ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Work);

        loaded.Should().NotBeNull();
        loaded!.Content.Should().Be("Prefer squash commits.");
        loaded.OverCap.Should().BeFalse();
    }

    [Fact]
    public void An_over_cap_marker_is_stripped_and_reported_as_over_cap()
    {
        ProjectDetails project = ProjectWithHome();
        Directory.CreateDirectory(ProjectHomePaths.PromptAddendaDirectory(_home));
        File.WriteAllText(
            ProjectHomePaths.PromptAddendumFile(_home, PromptBuilderKey.Agent.Value),
            $"{ProjectPromptAddendaLoader.OverCapMarker}\nA very long house style, kept anyway.");

        LoadedPromptAddendum? loaded = ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Agent);

        loaded.Should().NotBeNull();
        loaded!.OverCap.Should().BeTrue();
        loaded.Content.Should().Be("A very long house style, kept anyway.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_materialized_file_over_the_hard_ceiling_loads_truncated_with_the_label(bool withMarker)
    {
        ProjectDetails project = ProjectWithHome();
        string tooLong = new('x', ProjectDecider.PromptAddendumHardMaximumLength + 500);
        WriteAddendum(PromptBuilderKey.Agent, withMarker
            ? $"{ProjectPromptAddendaLoader.OverCapMarker}\n{tooLong}"
            : tooLong);

        LoadedPromptAddendum? loaded = ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Agent);

        loaded.Should().NotBeNull();
        loaded!.OverCap.Should().Be(withMarker);
        loaded.Content.Should().StartWith(new string('x', ProjectDecider.PromptAddendumHardMaximumLength));
        loaded.Content.Should().EndWith(ProjectPromptAddendaLoader.TruncationLabel);
        loaded.Content.Length.Should().Be(
            ProjectDecider.PromptAddendumHardMaximumLength + 2 + ProjectPromptAddendaLoader.TruncationLabel.Length);
    }

    [Fact]
    public void A_file_exactly_at_the_hard_ceiling_loads_unchanged()
    {
        ProjectDetails project = ProjectWithHome();
        string atCeiling = new('x', ProjectDecider.PromptAddendumHardMaximumLength);
        WriteAddendum(PromptBuilderKey.Agent, $"{ProjectPromptAddendaLoader.OverCapMarker}\n{atCeiling}");

        ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Agent)!.Content.Should().Be(atCeiling);
    }

    [Fact]
    public void The_one_live_addendum_of_1557_characters_loads_unchanged()
    {
        ProjectDetails project = ProjectWithHome();
        string liveSized = string.Join('\n', Enumerable.Repeat(new string('y', 77), 20))[..1557];
        WriteAddendum(PromptBuilderKey.Agent, liveSized);

        ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Agent)!.Content.Should().Be(liveSized);
    }

    [Fact]
    public void A_blank_materialized_file_reads_as_no_addendum()
    {
        ProjectDetails project = ProjectWithHome();
        WriteAddendum(PromptBuilderKey.Work, "   ");

        ProjectPromptAddendaLoader.TryLoad(project, PromptBuilderKey.Work).Should().BeNull();
    }

    private ProjectDetails ProjectWithHome()
    {
        Directory.CreateDirectory(_home);
        return new ProjectDetails { HomeDirectory = ProjectHome.Parse(_home) };
    }

    private void WriteAddendum(PromptBuilderKey builder, string content)
    {
        Directory.CreateDirectory(ProjectHomePaths.PromptAddendaDirectory(_home));
        File.WriteAllText(ProjectHomePaths.PromptAddendumFile(_home, builder.Value), content);
    }
}
