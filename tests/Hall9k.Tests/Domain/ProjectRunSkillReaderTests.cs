using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one read that decides whether a review may drive (idea b9b09779): what the project's own
/// ledger record says, off this node's projection. DB-free — the projection document is the whole
/// input, and <c>ProjectDetailsProjection</c> has its own tests for how the record gets there.
/// </summary>
public sealed class ProjectRunSkillReaderTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_project_with_no_record_has_no_run_skill()
    {
        ProjectRunSkillReader.Read(new ProjectDetails()).Should().BeNull();
    }

    [Theory]
    [InlineData("pointer")]
    [InlineData("full-text")]
    public void A_recorded_skill_is_handed_back_verbatim(string shape)
    {
        ProjectRunSkillReader.Read(WithSkill(RunSkillShape.Parse(shape), "## Launch\n\n`make dev`\n"))
            .Should().Be("## Launch\n\n`make dev`\n");
    }

    /// <summary>
    /// The platform's own mechanical "nothing in this repository says how to run it" record. Its
    /// content is an account of where the survey looked, not a procedure, so a driving session
    /// handed it would be following the statement that there is nothing to follow.
    /// </summary>
    [Fact]
    public void A_none_discoverable_record_reads_as_no_run_skill()
    {
        ProjectRunSkillReader.Read(
                WithSkill(RunSkillShape.NoneDiscoverable, "Nothing in this repository says how to run it."))
            .Should().BeNull();
    }

    private static ProjectDetails WithSkill(RunSkillShape shape, string content) => new()
    {
        Name = "hall9k",
        RunSkill = new ProjectRunSkill(content, shape, "abc123", RunSkillAuthor.Platform, RecordedAt, Guid.Empty),
    };
}
