using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Task a56cf16e: the refusals a node prints when it cannot fetch history it does not hold. The
/// origin incident is a message that was true about the mechanism and false about the outcome —
/// <c>h9k task add --from-issue</c> told a node with no owner root that the task "will appear on
/// this node's own board once catch-up brings that stream in", having queued nothing and being
/// unable to queue anything. These are pure string functions precisely so their wording is
/// checkable without a database, a daemon, or a second node.
/// </summary>
public sealed class EventStreamCatchUpRefusalTests
{
    [Fact]
    public void The_from_issue_refusal_names_project_join_and_denies_queueing_when_this_node_has_no_owner_root()
    {
        string refusal = EventStreamCatchUp.RecordedElsewhereRefusal(
            "github-issue:Hallmanac/hall9k#447", "ec35ceca", "hall9k",
            EventStreamCatchUp.RequestDisposition.NoOwnerRoot, projectEligibleForMessaging: true);

        refusal.Should().Contain("github-issue:Hallmanac/hall9k#447");
        refusal.Should().Contain("ec35ceca");
        refusal.Should().Contain("h9k project join hall9k");
        refusal.Should().Contain("nothing was queued");
        refusal.Should().NotContain(
            "will appear",
            "a node that queued nothing must never promise the stream turns up on its own");
    }

    // The disposition is an internal enum and an xUnit test method has to be public, so this takes
    // the discriminator as a bool rather than exposing the type through a public signature.
    [Theory]
    [InlineData(false, "on its way to this project's other members now")]
    [InlineData(true, "already on its way")]
    public void The_from_issue_refusal_says_whether_this_run_queued_the_request_or_found_one_standing(
        bool alreadyOutstanding, string expected)
    {
        string refusal = EventStreamCatchUp.RecordedElsewhereRefusal(
            "github-issue:Hallmanac/hall9k#447", "ec35ceca", "hall9k",
            alreadyOutstanding
                ? EventStreamCatchUp.RequestDisposition.AlreadyOutstanding
                : EventStreamCatchUp.RequestDisposition.Queued,
            projectEligibleForMessaging: true);

        refusal.Should().Contain(expected);
        refusal.Should().Contain("re-run this command afterward");
        refusal.Should().NotContain(
            "h9k project join", "this node can ask, so nothing here is waiting on a join");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, low — swept from the two pull
    /// commands into this refusal, which reaches the same dead end: an archived project is still
    /// resolvable and its ledger still readable, so the request queues for a project
    /// <c>MessageSweepEngine</c> never flushes, and "on its way to this project's other members"
    /// would be false rather than merely optimistic.
    /// </summary>
    [Fact]
    public void The_from_issue_refusal_never_says_a_request_is_on_its_way_for_a_project_the_sweep_cannot_flush()
    {
        string refusal = EventStreamCatchUp.RecordedElsewhereRefusal(
            "github-issue:Hallmanac/hall9k#447", "ec35ceca", "hall9k",
            EventStreamCatchUp.RequestDisposition.Queued, projectEligibleForMessaging: false);

        refusal.Should().Contain("eligible for messaging");
        refusal.Should().NotContain(
            "on its way", "nothing has been sent, and the sweep will not send it while the project is ineligible");
    }

    [Fact]
    public void The_task_pull_refusal_names_project_join_and_the_command_to_retype()
    {
        string refusal = EventStreamCatchUp.TaskPullBlockedRefusal(
            "01a0afc1-fc97-73b5-836e-39b8ec35ceca", "hall9k");

        refusal.Should().Contain("h9k project join hall9k");
        refusal.Should().Contain("h9k task pull 01a0afc1-fc97-73b5-836e-39b8ec35ceca");
        refusal.Should().Contain("nothing was queued");
    }

    [Fact]
    public void The_project_pull_refusal_names_project_join_and_the_command_to_retype()
    {
        string refusal = EventStreamCatchUp.ProjectPullBlockedRefusal("hall9k");

        refusal.Should().Contain("h9k project join hall9k");
        refusal.Should().Contain("h9k project pull hall9k --since");
        refusal.Should().Contain("nothing was queued");
    }

    /// <summary>Independent pre-PR review, cycle 4, adversarial lens, medium: the shared refusal
    /// for a stream this node holds only the tail of has to say that nothing was queued and why no
    /// ask would help, rather than pointing at a re-run that can never change the answer.</summary>
    [Fact]
    public void The_partially_held_refusal_says_nothing_was_queued_and_why_an_ask_cannot_help()
    {
        string refusal = EventStreamCatchUp.PartiallyHeldRefusal("ec35ceca", "h9k task show ec35ceca.");

        refusal.Should().Contain("ec35ceca");
        refusal.Should().Contain("only partly on this node");
        refusal.Should().Contain("Nothing was queued");
        refusal.Should().Contain("cannot be put in front of it");
        refusal.Should().NotContain(
            "re-run", "no re-run of anything changes what a node can be sent; saying so would be the old lie again");
    }

    [Theory]
    [InlineData("all", 0L)]
    [InlineData("ALL", 0L)]
    [InlineData(" all ", 0L)]
    [InlineData("0", 0L)]
    [InlineData("28273", 28273L)]
    public void Project_pull_reads_since_as_all_or_a_non_negative_global_sequence(string since, long expected) =>
        ProjectPullCommand.ParseSince(since).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-1")]
    [InlineData("everything")]
    [InlineData("28_273")]
    public void Project_pull_refuses_a_since_value_it_cannot_read(string since)
    {
        Action parse = () => ProjectPullCommand.ParseSince(since);

        parse.Should().Throw<DomainValidationException>().Which.Message.Should().Contain("--since");
    }
}
