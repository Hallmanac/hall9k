using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Reading the run's handoff off the agent's own session-end result (Decisions Log #36):
/// the last marker wins because agents quote the instructions, everything after it is the
/// handoff, and a result carrying no marker parses to null rather than to an empty string
/// the closeout append would have to interpret.
/// </summary>
public sealed class HandoffParserTests
{
    [Fact]
    public void Everything_after_the_marker_is_the_handoff()
    {
        string? handoff = HandoffParser.Parse(
            """
            I added the projection and its migration.

            HANDOFF:
            The projection is Inline, so a stream that stops receiving events keeps the
            document shape it was last written with.

            Left undone: the backfill, deliberately — nothing reads the new column yet.
            """);

        handoff.Should().StartWith("The projection is Inline");
        handoff.Should().Contain("Left undone: the backfill");
        handoff.Should().NotContain("I added the projection", "the summary above the marker is not the handoff");
    }

    [Fact]
    public void Text_on_the_marker_line_itself_is_kept()
    {
        HandoffParser.Parse("HANDOFF: Nothing surprising here.")
            .Should().Be("Nothing surprising here.", "a one-line handoff needs no second line to be real");
    }

    [Fact]
    public void The_last_marker_wins_because_agents_quote_the_instructions_first()
    {
        string? handoff = HandoffParser.Parse(
            """
            My plan was to end with a line reading exactly:

                HANDOFF:

            and that is what I am doing now.

            HANDOFF:
            The retry budget resets on a manual resolve.
            """);

        handoff.Should().Be("The retry budget resets on a manual resolve.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I did the work and wrote the tests. No marker anywhere.")]
    [InlineData("HANDOFF:\n\n   \n")]
    public void A_result_with_no_usable_handoff_parses_to_null_not_to_empty(string? summary) =>
        HandoffParser.Parse(summary).Should().BeNull(
            "an absent handoff is recorded as absent; an empty string is a fact nobody can interpret");

    [Fact]
    public void An_over_long_handoff_is_truncated_with_the_truncation_stated()
    {
        Guid runId = DomainId.New();
        string runDirectory = RunPaths.GlobalDirectory(runId);
        string bounded = HandoffParser.BoundForEvent(new string('x', HandoffParser.MaxEventLength + 500), runDirectory);

        bounded.Should().Contain("Truncated at", "a silently clipped handoff reads exactly like a complete one");
        bounded.Should().Contain(runId.ToString(), "the truncation names where the whole copy lives");
    }

    [Fact]
    public void A_handoff_within_budget_travels_verbatim()
    {
        const string handoff = "Short, which is what the prompt asks for.";
        HandoffParser.BoundForEvent(handoff, RunPaths.GlobalDirectory(DomainId.New())).Should().Be(handoff);
    }

    /// <summary>
    /// The composed handoff is what the task hands down, so a single contributor pays nothing
    /// for the multi-run case it is not: it renders as its own text, unframed.
    /// </summary>
    [Fact]
    public void One_runs_handoff_composes_to_itself()
    {
        const string handoff = "The rename is deliberate.";
        Guid runId = DomainId.New();
        HandoffParser.Compose(
            [new HandoffParser.RunHandoff(runId, RunPaths.GlobalDirectory(runId), handoff)]).Should().Be(handoff);
    }

    /// <summary>
    /// Several runs on one pull request compose in the order they are given, each labelled with
    /// its run so a reader can go find the whole copy. The order is the caller's dispatch
    /// order, which puts the run that built the work ahead of the follow-ups that reviewed it.
    /// </summary>
    [Fact]
    public void Several_runs_compose_in_order_each_labelled_with_its_run()
    {
        Guid original = DomainId.New();
        Guid followUp = DomainId.New();

        string composed = HandoffParser.Compose(
        [
            new HandoffParser.RunHandoff(original, RunPaths.GlobalDirectory(original), "The column is named Canonical."),
            new HandoffParser.RunHandoff(followUp, RunPaths.GlobalDirectory(followUp), "Resolved three review threads."),
        ]);

        composed.Should().Contain("carried by 2 runs");
        composed.Should().Contain(original.ToString("N")[^8..]).And.Contain(followUp.ToString("N")[^8..]);
        composed.IndexOf("The column is named Canonical.", StringComparison.Ordinal).Should().BeLessThan(
            composed.IndexOf("Resolved three review threads.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Truncating a composed handoff can cut a later run's section away entirely, so the note
    /// names every run directory the text was drawn from rather than only the first.
    /// </summary>
    [Fact]
    public void A_truncated_composition_names_every_run_it_was_drawn_from()
    {
        Guid original = DomainId.New();
        Guid followUp = DomainId.New();

        string bounded = HandoffParser.BoundForEvent(
            new string('x', HandoffParser.MaxEventLength + 500),
            [RunPaths.GlobalDirectory(original), RunPaths.GlobalDirectory(followUp)]);

        bounded.Should().Contain("The whole handoffs are at");
        bounded.Should().Contain(original.ToString()).And.Contain(followUp.ToString());
    }
}
