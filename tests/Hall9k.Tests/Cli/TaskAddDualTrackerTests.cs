using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Which of a dual --from-issue/--from-jira adoption becomes primary (task: a task may link to
/// both a GitHub issue and a Jira card): <see cref="TaskAddCommand.ResolvePrimary"/> is the pure
/// decision this task add pulls the answer from, once the project (and its own
/// <c>--primary-tracker</c> default) is known — this is where the override-beats-default chain and
/// the "nobody said" refusal are actually checked, without needing a document store or a real
/// tracker connection.
/// </summary>
public sealed class TaskAddDualTrackerTests
{
    private static readonly TaskAddCommand.AdoptionSource Issue =
        new(WorkItemProvider.GitHub, "Hallmanac/hall9k#42", "--from-issue", "issue");

    private static readonly TaskAddCommand.AdoptionSource Jira =
        new(WorkItemProvider.Jira, "PROJ-123", "--from-jira", "card");

    [Fact]
    public void The_per_task_override_names_the_primary_when_the_project_has_no_default()
    {
        (TaskAddCommand.AdoptionSource primary, TaskAddCommand.AdoptionSource secondary) =
            TaskAddCommand.ResolvePrimary(Issue, Jira, "jira", WorkItemProvider.Unknown);

        primary.Should().Be(Jira);
        secondary.Should().Be(Issue);
    }

    [Fact]
    public void The_project_default_names_the_primary_when_no_override_is_given()
    {
        (TaskAddCommand.AdoptionSource primary, TaskAddCommand.AdoptionSource secondary) =
            TaskAddCommand.ResolvePrimary(Issue, Jira, primaryTrackerOverride: null, WorkItemProvider.GitHub);

        primary.Should().Be(Issue);
        secondary.Should().Be(Jira);
    }

    [Fact]
    public void The_per_task_override_beats_the_project_default_when_both_are_set()
    {
        (TaskAddCommand.AdoptionSource primary, TaskAddCommand.AdoptionSource secondary) =
            TaskAddCommand.ResolvePrimary(Issue, Jira, "jira", WorkItemProvider.GitHub);

        primary.Should().Be(Jira, "the invocation's own --primary-tracker outranks the project's default");
        secondary.Should().Be(Issue);
    }

    [Fact]
    public void Neither_an_override_nor_a_default_is_refused_rather_than_guessed_at()
    {
        Action resolve = () => TaskAddCommand.ResolvePrimary(
            Issue, Jira, primaryTrackerOverride: null, WorkItemProvider.Unknown);

        resolve.Should().Throw<DomainValidationException>()
            .WithMessage("*need to know which one is primary*");
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("GITHUB")]
    [InlineData(" github ")]
    public void The_override_reads_case_and_whitespace_insensitively(string value)
    {
        (TaskAddCommand.AdoptionSource primary, _) = TaskAddCommand.ResolvePrimary(
            Issue, Jira, value, WorkItemProvider.Unknown);

        primary.Should().Be(Issue);
    }

    [Fact]
    public void An_override_outside_the_two_trackers_is_refused()
    {
        Action resolve = () => TaskAddCommand.ResolvePrimary(Issue, Jira, "trello", WorkItemProvider.Unknown);

        resolve.Should().Throw<DomainValidationException>().WithMessage("*must be github or jira*");
    }
}
