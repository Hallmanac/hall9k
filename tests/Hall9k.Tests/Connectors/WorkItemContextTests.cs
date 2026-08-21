using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The provenance header is the never-guess rule made readable (AGENTS.md): an agent handed an
/// issue body months after the import must not read it as the issue's current state. These
/// assertions are about what the text promises, because that is the whole artifact.
/// </summary>
public sealed class WorkItemContextTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 21, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_header_stamps_the_state_with_the_moment_it_was_read()
    {
        string context = WorkItemContext.Compose(Item("The board already holds this work."));

        context.Should().Contain("Imported from github:Hallmanac/hall9k#42.")
            .And.Contain("State as observed at import (2026-08-21 09:30:00Z): open")
            .And.Contain("does not track the item afterwards")
            .And.Contain("https://github.com/Hallmanac/hall9k/issues/42")
            .And.Contain("The board already holds this work.");
    }

    [Fact]
    public void An_empty_body_says_so_rather_than_leaving_a_blank_the_agent_must_interpret()
    {
        string context = WorkItemContext.Compose(Item(body: null));

        context.Should().Contain("The item had no description when it was imported.");
    }

    [Fact]
    public void The_humans_own_context_lands_after_the_source_material()
    {
        string context = WorkItemContext.Compose(Item("Issue text"), "Start with the projection, not the endpoint.");

        context.IndexOf("Issue text", StringComparison.Ordinal)
            .Should().BeLessThan(context.IndexOf("Start with the projection", StringComparison.Ordinal),
                "the operator's instruction reads on top of the source, not as part of it");
    }

    [Fact]
    public void An_unknown_state_reads_as_unknown_rather_than_as_open()
    {
        string context = WorkItemContext.Compose(Item("Body") with { Status = WorkItemStatus.Unknown });

        context.Should().Contain("State as observed at import (2026-08-21 09:30:00Z): unknown");
    }

    [Fact]
    public void The_body_keeps_its_own_trailing_whitespace()
    {
        string context = WorkItemContext.Compose(Item("Two spaces end this line, which is a Markdown break  \n"));

        context.Should().EndWith("Two spaces end this line, which is a Markdown break  \n",
            "Compose trims the text it adds, never the source body it promised verbatim");
    }

    [Fact]
    public void The_body_reads_the_same_whether_or_not_the_human_added_context()
    {
        ImportedWorkItem item = Item("Issue text  \n");

        string alone = WorkItemContext.Compose(item);
        string withContext = WorkItemContext.Compose(item, "  Start with the projection, not the endpoint.  ");

        withContext.Should().StartWith(alone,
            "how the human invoked import must not change the copy of the issue the agent is handed");
        withContext.Should().EndWith(
            $"{Environment.NewLine}{Environment.NewLine}Start with the projection, not the endpoint.");
    }

    private static ImportedWorkItem Item(string? body) => new(
        new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#42"),
        "Adopt existing GitHub issues",
        body,
        WorkItemStatus.Open,
        new Uri("https://github.com/Hallmanac/hall9k/issues/42"),
        ObservedAt);
}
