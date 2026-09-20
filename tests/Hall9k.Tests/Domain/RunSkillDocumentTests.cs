using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The run skill's shared shape and the rules every route into it goes through (idea b9b09779,
/// piece 4), DB-free.
/// </summary>
public sealed class RunSkillDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Body() =>
        "## Prerequisites\n\nThe .NET 10 SDK.\n\n"
        + "## One-time setup\n\ndotnet restore\n\n"
        + "## Launch\n\ndotnet run (README.md)\n\n"
        + "## How to know it is up\n\nThe port opens.\n\n"
        + "## Address or entry point\n\nhttp://localhost:5000\n\n"
        + "## Human steps\n\nNone.\n";

    [Fact]
    public void Compose_states_the_shape_on_the_first_line()
    {
        string document = RunSkillDocument.Compose(RunSkillShape.Pointer, Body());

        document.Split('\n')[0].Should().StartWith(RunSkillDocument.ShapeLinePrefix);
        document.Should().Contain("pointer.");
        document.Should().Contain("## Launch");
    }

    [Fact]
    public void A_shape_line_the_composer_typed_itself_is_replaced_rather_than_stated_twice()
    {
        // The daemon knows the shape from the trailer, so a session that also typed one must not
        // be able to leave a second, possibly disagreeing declaration in the document.
        string document = RunSkillDocument.Compose(
            RunSkillShape.FullText,
            $"{RunSkillDocument.ShapeLinePrefix} pointer. I decided this myself.\n\n{Body()}");

        document.Should().NotContain("I decided this myself");
        document.Split('\n').Count(line => line.StartsWith(RunSkillDocument.ShapeLinePrefix, StringComparison.Ordinal))
            .Should().Be(1);
        document.Should().Contain("full-text.");
    }

    [Fact]
    public void A_later_line_quoting_the_prefix_is_content_and_is_left_alone()
    {
        string document = RunSkillDocument.Compose(
            RunSkillShape.Pointer, Body() + $"\nThe first line reads '{RunSkillDocument.ShapeLinePrefix} pointer'.\n");

        document.Should().Contain($"The first line reads '{RunSkillDocument.ShapeLinePrefix} pointer'.");
    }

    [Fact]
    public void MissingHeadings_names_what_is_absent_and_ignores_the_markdown_level()
    {
        RunSkillDocument.MissingHeadings(Body()).Should().BeEmpty();
        RunSkillDocument.MissingHeadings(Body().Replace("## Launch", "#### Launch", StringComparison.Ordinal))
            .Should().BeEmpty("the contract is that the section is named, not how deeply it nests");
        RunSkillDocument.MissingHeadings("## Launch\n\ndotnet run\n")
            .Should().Equal("Prerequisites", "One-time setup", "How to know it is up", "Address or entry point", "Human steps");
    }

    [Fact]
    public void The_none_discoverable_document_says_so_in_every_section_and_names_what_was_looked_for()
    {
        string document = RunSkillDocument.ComposeNoneDiscoverable(["a README", "a build file"]);

        RunSkillDocument.MissingHeadings(document).Should().BeEmpty();
        document.Split('\n')[0].Should().Contain("none-discoverable");
        document.Should().Contain("- a README");
        document.Should().Contain("- a build file");
    }

    [Fact]
    public void RecordRunSkill_refuses_a_document_missing_a_shared_section()
    {
        Action act = () => ProjectDecider.RecordRunSkill(
            DomainId.New(), "## Launch\n\ndotnet run\n", RunSkillShape.FullText, RunSkillAuthor.Hand, null,
            DomainId.New(), Now);

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*Prerequisites*")
            .WithMessage("*Human steps*");
    }

    [Fact]
    public void RecordRunSkill_refuses_blank_content_an_unknown_shape_and_an_unknown_author()
    {
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();

        FluentActions.Invoking(() => ProjectDecider.RecordRunSkill(
                projectId, "   ", RunSkillShape.FullText, RunSkillAuthor.Hand, null, ownerId, Now))
            .Should().Throw<DomainValidationException>().WithMessage("*none-discoverable skill*");

        FluentActions.Invoking(() => ProjectDecider.RecordRunSkill(
                projectId, Body(), RunSkillShape.Unknown, RunSkillAuthor.Hand, null, ownerId, Now))
            .Should().Throw<DomainValidationException>().WithMessage("*real shape*");

        FluentActions.Invoking(() => ProjectDecider.RecordRunSkill(
                projectId, Body(), RunSkillShape.FullText, RunSkillAuthor.Unknown, null, ownerId, Now))
            .Should().Throw<DomainValidationException>().WithMessage("*real author*");
    }

    [Fact]
    public void RecordRunSkill_refuses_a_document_past_the_cap()
    {
        string huge = Body() + new string('x', ProjectDecider.RunSkillMaximumLength);

        FluentActions.Invoking(() => ProjectDecider.RecordRunSkill(
                DomainId.New(), huge, RunSkillShape.FullText, RunSkillAuthor.Hand, null, DomainId.New(), Now))
            .Should().Throw<DomainValidationException>()
            .WithMessage($"*{ProjectDecider.RunSkillMaximumLength}-character cap*");
    }

    [Fact]
    public void An_unobserved_commit_is_recorded_blank_rather_than_filled_in()
    {
        ProjectRunSkillRecorded recorded = ProjectDecider.RecordRunSkill(
            DomainId.New(), Body(), RunSkillShape.FullText, RunSkillAuthor.Hand, composedAgainstCommit: null,
            DomainId.New(), Now);

        recorded.ComposedAgainstCommit.Should().BeEmpty();
    }

    [Fact]
    public void FailRunSkillDiscovery_needs_a_reason()
    {
        FluentActions.Invoking(() => ProjectDecider.FailRunSkillDiscovery(DomainId.New(), "  ", Now))
            .Should().Throw<DomainValidationException>().WithMessage("*reason it failed*");
    }

    [Fact]
    public void Shape_parses_strictly_and_reads_an_unrecognized_stream_value_as_unknown()
    {
        RunSkillShape.Parse("full-text").Should().Be(RunSkillShape.FullText);
        RunSkillShape.Parse(" Pointer ").Should().Be(RunSkillShape.Pointer);
        FluentActions.Invoking(() => RunSkillShape.Parse("pointerish"))
            .Should().Throw<DomainValidationException>().WithMessage("*not a run-skill shape*");
        RunSkillShape.FromInput("something-later-added").Should().Be(RunSkillShape.Unknown);
    }

    [Fact]
    public void The_aggregate_settles_a_request_on_a_recorded_skill_and_on_a_failure()
    {
        Guid projectId = DomainId.New();
        Guid ownerId = DomainId.New();
        ProjectAggregate project = new();
        project.Apply(new ProjectRunSkillDiscoveryRequested(projectId, Now, ownerId));
        project.RunSkillDiscoveryRequestedAt.Should().Be(Now);

        project.Apply(ProjectDecider.RecordRunSkill(
            projectId, Body(), RunSkillShape.Pointer, RunSkillAuthor.DiscoverySession, "abc123", ownerId, Now));

        project.RunSkill!.Shape.Should().Be(RunSkillShape.Pointer);
        project.RunSkill.ComposedAgainstCommit.Should().Be("abc123");
        project.RunSkillDiscoveryRequestedAt.Should().BeNull();

        project.Apply(new ProjectRunSkillDiscoveryRequested(projectId, Now.AddHours(1), ownerId));
        project.Apply(ProjectDecider.FailRunSkillDiscovery(projectId, "the session timed out", Now.AddHours(2)));

        project.RunSkillDiscoveryRequestedAt.Should().BeNull();
        project.RunSkillDiscoveryFailure.Should().Be("the session timed out");
        project.RunSkill.Should().NotBeNull("a failed re-discovery never erases the skill already recorded");
    }
}
