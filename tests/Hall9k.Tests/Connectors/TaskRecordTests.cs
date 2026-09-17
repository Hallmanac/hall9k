using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The record a published task writes into the project's own ledger, and what any node that
/// shares the project reads back out of it (idea 202383dc, A3a: a task's record lives at
/// <c>records/&lt;task-id&gt;.yaml</c> on <c>refs/hall9k/ledger/records</c>, not in a collapsed
/// section of a tracker item). What is pinned here is the round trip through
/// <see cref="TaskRecord.ToYaml"/>/<see cref="TaskRecord.TryParse"/> — every field, including the
/// ones this record used to write differently (a dependency is a task id now, never an issue
/// number; the epic and the holder both travel as ids) — and the same forward-compatibility and
/// never-guess behaviour the retired issue-block writer already had to get right: a reader
/// degrades gracefully rather than half-inventing a fact nobody actually observed.
/// </summary>
public sealed class TaskRecordTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);

    private static readonly TaskOrigin Origin = new(
        Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
        "HALLMANAC-MAC",
        Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
        "task/7325335a-a-published-task-s-record",
        PublishedAt);

    private static TaskRecord Sample(
        string? context = "Origin: Brian, tired of copying criteria by hand.",
        TaskRecordCaps? caps = null,
        string? epicTitle = null,
        Guid? epicId = null,
        IReadOnlyList<Guid>? dependencies = null,
        PreApprovalMode? preApproval = null,
        ExternalReference? externalReference = null,
        TaskRecordHolder? holder = null,
        string ownerFingerprint = "abc123fingerprint") => new(
        Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
        "hall9k",
        "feature",
        "A task's record lives in the project's own ledger",
        [
            "Publishing writes it to records/<task-id>.yaml",
            "Any node that shares the project reads it back",
        ],
        context,
        "claude-opus-5",
        preApproval ?? PreApprovalMode.On,
        externalReference,
        dependencies ?? [],
        epicTitle,
        epicId,
        caps ?? TaskRecordCaps.None,
        ownerFingerprint,
        Origin,
        holder);

    [Fact]
    public void A_record_round_trips_through_its_own_yaml()
    {
        TaskRecord written = Sample(
            caps: new TaskRecordCaps(3, 2, 2, 12, 4),
            epicTitle: "Distributed team on the tracker",
            epicId: Guid.Parse("01a06f2a-0000-7000-8000-00000000000e"),
            dependencies: [Guid.Parse("01a06f2a-0000-7000-8000-000000000081"), Guid.Parse("01a06f2a-0000-7000-8000-000000000082")],
            externalReference: new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#266"),
            holder: new TaskRecordHolder(
                "def456fingerprint",
                Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335b"),
                "HALLMANAC-WIN",
                new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero)));

        TaskRecord? read = TaskRecord.TryParse(written.ToYaml());

        read.Should().BeEquivalentTo(written);
    }

    [Fact]
    public void A_record_with_no_external_reference_round_trips_as_untracked()
    {
        TaskRecord written = Sample(externalReference: null);

        TaskRecord? read = TaskRecord.TryParse(written.ToYaml());

        read!.ExternalReference.Should().BeNull(
            "a project with no tracker, or a task published --untracked, gets a record too (Brian, 2026-09-13)");
        written.ToYaml().Should().NotContain("external-reference");
    }

    [Fact]
    public void A_record_with_no_holder_round_trips_with_none()
    {
        TaskRecord written = Sample(holder: null);

        written.ToYaml().Should().NotContain("holder-");
        TaskRecord.TryParse(written.ToYaml())!.Holder.Should().BeNull();
    }

    [Fact]
    public void The_record_is_written_with_plain_and_block_scalars_and_never_quoted_ones()
    {
        string yaml = Sample(context: "One paragraph.\n\nAnd a second: with a colon in it.").ToYaml();

        yaml.Should().NotContain("\"", "a quoted scalar is what the 2026-09-06 adoption of #81 and #82 tripped on")
            .And.Contain("hall9k-task-record: 1")
            .And.Contain("context: |");
    }

    [Fact]
    public void The_type_is_written_lowercase_and_either_casing_is_accepted_on_read()
    {
        TaskRecord record = Sample() with { Type = "Feature" };

        record.ToYaml().Should().Contain("type: feature");
        TaskRecord.TryParse(record.ToYaml())!.Type.Should().Be("feature");
        TaskRecord.TryParse(record.ToYaml().Replace("type: feature", "type: Feature"))!.Type
            .Should().Be("feature", "a block hand-written in the platform's display casing still adopts");
    }

    [Theory]
    [InlineData("On")]
    [InlineData("Off")]
    [InlineData("AfterHumanReview")]
    public void Every_pre_approval_mode_round_trips(string mode)
    {
        TaskRecord written = Sample(preApproval: (PreApprovalMode)mode);

        TaskRecord.TryParse(written.ToYaml())!.PreApproval.Should().Be((PreApprovalMode)mode);
    }

    [Fact]
    public void A_quoted_record_written_by_hand_still_reads_without_its_quote_characters()
    {
        const string HandWritten = """
            hall9k-task-record: 1
            project: hall9k
            type: feature
            pre-approved: true
            objective: "Adopt the issue and get the same task"
            criteria:
              - "Publishing writes the record"
              - "Adoption reads it"
            """;

        TaskRecord? read = TaskRecord.TryParse(HandWritten);

        read!.Objective.Should().Be("Adopt the issue and get the same task");
        read.Criteria.Should().Equal("Publishing writes the record", "Adoption reads it");
    }

    /// <summary>
    /// A hand-annotated block that spaces its criteria out — legal YAML, and the natural way to
    /// write a long contract by hand — used to lose every criterion after the first blank line, so
    /// the adopted draft carried a shorter readiness contract than the origin wrote and nothing
    /// said so (independent pre-PR review, cycle 1, adversarial lens). Kept here even though the
    /// record no longer travels through an issue body: the same line-oriented grammar
    /// (<see cref="Hall9k.Connectors.Text.FrontmatterYaml"/>) still reads it either way.
    /// </summary>
    [Fact]
    public void Criteria_spaced_out_by_hand_all_survive_the_read()
    {
        const string HandWritten = """
            hall9k-task-record: 1
            project: hall9k
            type: feature
            objective: Adopt the issue and get the same task

            criteria:
              - Publishing writes the record

              - Adoption reads it once

              - h9k task show: says which install published it

            context: The record is read once and never re-checked.
            """;

        TaskRecord? read = TaskRecord.TryParse(HandWritten);

        read!.Criteria.Should().Equal(
            "Publishing writes the record",
            "Adoption reads it once",
            "h9k task show: says which install published it");
        read.AgentContext.Should().Be("The record is read once and never re-checked.");
    }

    [Fact]
    public void Text_with_no_version_key_is_not_a_record_at_all()
    {
        TaskRecord.TryParse("project: hall9k\nobjective: something\n").Should().BeNull();
        TaskRecord.TryParse(null).Should().BeNull();
        TaskRecord.TryParse(string.Empty).Should().BeNull();
    }

    [Fact]
    public void A_record_with_no_objective_is_refused_rather_than_half_read()
    {
        TaskRecord.TryParse("hall9k-task-record: 1\nproject: hall9k\n").Should().BeNull(
            "a draft cannot be built around a missing objective, and half a record is worse than none");
    }

    [Fact]
    public void Dependencies_are_task_ids_never_issue_numbers()
    {
        Guid first = Guid.Parse("01a06f2a-0000-7000-8000-000000000081");
        Guid second = Guid.Parse("01a06f2a-0000-7000-8000-000000000082");

        string yaml = Sample(dependencies: [first, second]).ToYaml();

        yaml.Should().Contain($"dependencies: [{first}, {second}]");
        Sample().ToYaml().Should().Contain("dependencies: []");

        TaskRecord? read = TaskRecord.TryParse(yaml);
        read!.Dependencies.Should().Equal(first, second);
    }

    [Fact]
    public void An_unparseable_dependency_is_dropped_rather_than_guessed_at()
    {
        TaskRecord? read = TaskRecord.TryParse(
            Sample().ToYaml().Replace("dependencies: []", "dependencies: [not-a-guid]"));

        read!.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void The_epic_travels_as_an_id_beside_its_own_title()
    {
        Guid epicId = Guid.Parse("01a06f2a-0000-7000-8000-00000000000e");

        string yaml = Sample(epicTitle: "Distributed team on the tracker", epicId: epicId).ToYaml();

        yaml.Should().Contain("epic-title: Distributed team on the tracker")
            .And.Contain($"epic-id: {epicId}");

        TaskRecord? read = TaskRecord.TryParse(yaml);
        read!.EpicId.Should().Be(epicId);
        read.EpicTitle.Should().Be("Distributed team on the tracker");
    }

    [Fact]
    public void A_record_with_no_epic_writes_neither_field()
    {
        Sample().ToYaml().Should().NotContain("epic-title").And.NotContain("epic-id");
    }

    [Fact]
    public void Caps_are_written_only_where_the_task_actually_overrode_one()
    {
        Sample().ToYaml().Should().NotContain("session-cap")
            .And.NotContain("max-compliance-review-cycles");

        string yaml = Sample(caps: new TaskRecordCaps(null, null, null, null, 4)).ToYaml();

        yaml.Should().Contain("session-cap: 4").And.NotContain("max-final-full-pass-rounds");
    }

    [Fact]
    public void The_origin_travels_whole_so_a_mirror_can_be_told_apart_from_local_work()
    {
        TaskRecord? read = TaskRecord.TryParse(Sample().ToYaml());

        read!.Origin.NodeId.Should().Be(Origin.NodeId);
        read.Origin.NodeName.Should().Be("HALLMANAC-MAC");
        read.Origin.TaskId.Should().Be(Origin.TaskId);
        read.Origin.BranchName.Should().Be("task/7325335a-a-published-task-s-record");
        read.Origin.PublishedAt.Should().Be(PublishedAt);
    }

    [Fact]
    public void The_origin_owner_fingerprint_round_trips()
    {
        TaskRecord? read = TaskRecord.TryParse(Sample(ownerFingerprint: "some-root-fingerprint").ToYaml());

        read!.OriginOwnerFingerprint.Should().Be("some-root-fingerprint");
    }

    [Fact]
    public void The_holder_block_round_trips_when_present()
    {
        TaskRecordHolder holder = new(
            "def456fingerprint",
            Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335b"),
            "HALLMANAC-WIN",
            new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero));
        TaskRecord written = Sample(holder: holder);

        TaskRecord? read = TaskRecord.TryParse(written.ToYaml());

        read!.Holder.Should().Be(holder);
    }

    [Fact]
    public void A_record_with_no_publish_stamp_reads_as_a_time_nobody_observed()
    {
        TaskRecord? read = TaskRecord.TryParse("hall9k-task-record: 1\nobjective: something\n");

        read!.Origin.PublishedAt.Should().Be(DateTimeOffset.MinValue,
            "stamping it with the moment of reading would claim an observation nobody made");
    }

    [Fact]
    public void A_holder_with_an_unreadable_since_stamp_reads_as_a_time_nobody_observed()
    {
        string yaml = Sample(holder: new TaskRecordHolder(
                "def456fingerprint", Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335b"), "HALLMANAC-WIN",
                new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero)))
            .ToYaml()
            .Replace("2026-09-10 08:00:00Z", "not-a-stamp");

        TaskRecord.TryParse(yaml)!.Holder!.Since.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void A_multi_paragraph_agent_context_survives_the_crossing_verbatim()
    {
        const string Context = "First paragraph, with a colon: here.\n\n  An indented second line.\n\nLast.";

        TaskRecord? read = TaskRecord.TryParse(Sample(context: Context).ToYaml());

        read!.AgentContext.Should().Be(Context);
    }
}
