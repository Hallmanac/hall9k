using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The pure half of adopting an issue that carries a task record: what the record, this install's
/// resolution of its cross-install references, and the command line come to together. The
/// store-backed half — resolving issue numbers to local tasks, an epic title to a local epic —
/// lives in <c>Integration.CrossInstallTaskRecordTests</c>.
/// </summary>
public sealed class TaskRecordAdoptionTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);

    private static readonly TaskRecordAdoption.Resolution NothingResolved =
        new([], [], null, null);

    private static readonly TaskRecordAdoption.Overrides NothingOnTheCommandLine =
        new(null, [], null, null, null, null, []);

    private const string Reference = "Hallmanac/hall9k#266";

    private static TaskRecord Record(string type) => new(
        "hall9k",
        type,
        "A published task's issue carries the whole task record",
        ["Publishing writes the block", "Adoption reads it once"],
        "Origin: Brian. The Mac publishes, the Windows node adopts.",
        null,
        PreApprovalMode.Off,
        [],
        0,
        null,
        null,
        TaskRecordCaps.None,
        new TaskOrigin(
            Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
            "HALLMANAC-MAC",
            Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
            "task/7325335a-a-published-task-s-github-issu",
            PublishedAt));

    [Fact]
    public void A_type_the_record_names_is_what_the_draft_takes()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("bugfix"), NothingResolved, NothingOnTheCommandLine, Reference);

        draft.Type.Should().Be("bugfix");
        draft.UnrecognizedType.Should().BeNull();
    }

    /// <summary>
    /// The record's stated forward compatibility, on the one field that used to break it: a type
    /// written by a later build ran through the command line's own strict parser and failed the
    /// whole adoption, quoting a <c>--type</c> flag the operator never passed (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public void A_type_from_a_later_build_degrades_instead_of_refusing_the_adoption()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("spike"), NothingResolved, NothingOnTheCommandLine, Reference);

        draft.Type.Should().BeNull("the caller's own type stands rather than a word this build cannot read");
        draft.UnrecognizedType.Should().Be("spike",
            "and the word is carried so the adoption output can name what it could not use");
        draft.Objective.Should().Be(Record("spike").Objective, "every other field still adopts");
        draft.Criteria.Should().HaveCount(2);
    }

    [Fact]
    public void An_unreadable_type_the_command_line_already_answered_is_not_reported_at_all()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("spike"),
            NothingResolved,
            NothingOnTheCommandLine with { Type = "feature" },
            Reference);

        draft.Type.Should().BeNull("the command line's own --type stands and is never restated here");
        draft.UnrecognizedType.Should().BeNull("nothing degraded — the record's type was never read");
    }

    [Fact]
    public void A_record_naming_pr_review_is_still_refused_with_the_two_routes_that_work()
    {
        Action reconstruct = () => TaskRecordAdoption.Reconstruct(
            Record("pr-review"), NothingResolved, NothingOnTheCommandLine, Reference);

        reconstruct.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("--from-pr").And.Contain("--type feature");
    }

    /// <summary>
    /// The same degrade the type field makes, on the two other fields that used to wall the whole
    /// adoption instead: a cap outside this build's floors reached
    /// <c>TaskDecider.OverrideSessionCap</c>/<c>OverrideReviewCaps</c> and threw, quoting cap flags
    /// <c>h9k task add</c> does not even have, and an ill-formed model name reached
    /// <c>TaskDecider.VetModel</c> and threw quoting <c>--model</c> (independent pre-PR review,
    /// cycle 1, both lenses). Reachable only from a hand-written block — which Decisions Log #151
    /// records as a supported shape, since ten issues were annotated that way before the feature
    /// existed.
    /// </summary>
    [Fact]
    public void A_cap_outside_this_builds_floors_degrades_instead_of_refusing_the_adoption()
    {
        TaskRecord record = Record("feature") with
        {
            Caps = new TaskRecordCaps(
                MaxComplianceReviewCycles: 2,
                MaxAdversarialReviewCycles: -1,
                MaxFinalFullPassRounds: null,
                LifetimeReviewCycleBudget: 0,
                SessionCap: 0),
        };

        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            record, NothingResolved, NothingOnTheCommandLine, Reference);

        draft.Caps.MaxComplianceReviewCycles.Should().Be(2, "a usable cap still carries over");
        draft.Caps.MaxAdversarialReviewCycles.Should().BeNull();
        draft.Caps.LifetimeReviewCycleBudget.Should().BeNull();
        draft.Caps.SessionCap.Should().BeNull();
        draft.UnusableCaps.Should().BeEquivalentTo(
            [
                new TaskRecordAdoption.UnusableCap("max-adversarial-review-cycles", -1),
                new TaskRecordAdoption.UnusableCap("lifetime-review-cycle-budget", 0),
                new TaskRecordAdoption.UnusableCap("session-cap", 0),
            ],
            "each dropped line is named so the adoption output can report what it could not use");
        draft.UnusableCaps[0].Command("a1b2c3d4").Should().Be(
            "h9k task set-review-caps a1b2c3d4 --max-adversarial-review-cycles <n>",
            "the record's keys are the review-cap flags' own names, so the message can name the fix");
        draft.UnusableCaps[2].Command("a1b2c3d4").Should().Be("h9k task set-session-cap a1b2c3d4 <cap>",
            "the session cap is the one that takes its value as an argument");
        draft.Objective.Should().Be(record.Objective, "every other field still adopts");
        draft.Criteria.Should().HaveCount(2);
    }

    [Fact]
    public void A_zero_per_run_review_cap_is_carried_because_zero_is_the_takeover_lever()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("feature") with { Caps = new TaskRecordCaps(0, 0, 0, null, null) },
            NothingResolved,
            NothingOnTheCommandLine,
            Reference);

        draft.Caps.Should().Be(new TaskRecordCaps(0, 0, 0, null, null));
        draft.UnusableCaps.Should().BeEmpty();
    }

    [Fact]
    public void A_model_this_build_will_not_spawn_degrades_instead_of_refusing_the_adoption()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("feature") with { Model = "opus; rm -rf /" },
            NothingResolved,
            NothingOnTheCommandLine,
            Reference);

        draft.Model.Should().BeNull("the node's own default decides rather than a name we will not spawn");
        draft.UnusableModel.Should().Be("opus; rm -rf /");
        draft.Objective.Should().Be(Record("feature").Objective);
    }

    [Fact]
    public void A_model_the_record_states_as_no_preference_is_not_reported_as_unusable()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("feature") with { Model = "default" },
            NothingResolved,
            NothingOnTheCommandLine,
            Reference);

        draft.Model.Should().BeNull();
        draft.UnusableModel.Should().BeNull("'default' is the word for no preference, not a value we failed to read");
    }

    [Fact]
    public void A_model_the_record_names_is_what_the_draft_takes()
    {
        TaskRecordAdoption.Reconstruction draft = TaskRecordAdoption.Reconstruct(
            Record("feature") with { Model = "opus" }, NothingResolved, NothingOnTheCommandLine, Reference);

        draft.Model.Should().Be("opus");
        draft.UnusableModel.Should().BeNull();
    }
}
