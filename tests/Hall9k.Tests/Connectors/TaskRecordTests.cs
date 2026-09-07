using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The record a published task writes into its tracker item, and what a second install reads back
/// out of it (task: a published task's GitHub issue carries the whole task record). The shape is
/// the contract between two machines that never talk to each other, so what these pin is the
/// round trip and the two identifiers that had to change form to survive the crossing —
/// dependencies as issue numbers, the epic as a title.
/// </summary>
public sealed class TaskRecordTests
{
    private static readonly DateTimeOffset PublishedAt = new(2026, 9, 7, 15, 31, 33, TimeSpan.Zero);

    private static readonly TaskOrigin Origin = new(
        Guid.Parse("01a07c7e-fed4-74bf-a0f8-ac5a7325335a"),
        "HALLMANAC-MAC",
        Guid.Parse("01a07909-b8a5-777d-9033-4318ba2a31b5"),
        "task/7325335a-a-published-task-s-github-issu",
        PublishedAt);

    private static TaskRecord Sample(
        string? context = "Origin: Brian, tired of copying criteria by hand.",
        TaskRecordCaps? caps = null,
        string? epicTitle = null,
        Guid? epicOriginId = null,
        IReadOnlyList<int>? blockedBy = null,
        int withoutIssues = 0,
        PreApprovalMode? preApproval = null) => new(
        "hall9k",
        "feature",
        "A published task's issue carries the whole task record: criteria, context, caps",
        [
            "Publishing writes a collapsed details section titled 'Hall9k task record'",
            "h9k task add --from-issue reads that block: criteria become criteria, not context",
        ],
        context,
        "claude-opus-5",
        preApproval ?? PreApprovalMode.On,
        blockedBy ?? [],
        withoutIssues,
        epicTitle,
        epicOriginId,
        caps ?? TaskRecordCaps.None,
        Origin);

    [Fact]
    public void A_record_round_trips_through_its_own_yaml()
    {
        TaskRecord written = Sample(
            caps: new TaskRecordCaps(3, 2, 2, 12, 4),
            epicTitle: "Distributed team on the tracker",
            epicOriginId: Guid.Parse("01a06f2a-0000-7000-8000-00000000000e"),
            blockedBy: [81, 82],
            withoutIssues: 2);

        TaskRecord? read = TaskRecord.TryParse(written.ToYaml());

        read.Should().BeEquivalentTo(written);
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

    [Fact]
    public void A_quoted_record_written_by_hand_still_reads_without_its_quote_characters()
    {
        // The eight issues annotated by hand on 2026-09-06 look like this. The writer never
        // produces it, and the reader has to cope with it anyway.
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
    /// The reference format, as the Mac window hand-wrote it into ten issues on 2026-09-06 and
    /// 2026-09-07 (#94, #103, #248, #249, #251, #252, #81, #82, #247, #265, and this feature's own
    /// #266) so the Windows node could adopt them before any of this existed. This build writes a
    /// different shape — flat origin keys rather than a nested mapping — and the point of this test
    /// is what a block in the OLD shape still yields: the whole readiness contract and the context,
    /// with the origin absent rather than half-invented, because a nested mapping is not something
    /// the line-oriented grammar reads and guessing at it would be worse than saying nothing.
    /// </summary>
    [Fact]
    public void The_hand_written_reference_format_still_yields_the_contract_it_can_state()
    {
        const string HandWritten = """
            hall9k-task-record: 1
            origin:
              node: mac (brianhallmanac)
              task: 01a07909-b8a5-777d-9033-4318ba2a31b5
              written: 2026-09-07 11:29 EDT
            project: hall9k
            type: feature
            pre-approved: true
            epic: none (idea 64c75e43, distributed team on the tracker)
            blocked-by-issues: []
            objective: "A published task's GitHub issue carries the whole task record"
            criteria:
              - "Publishing a task under the github-issues backlog policy writes the block"
              - "h9k task add --from-issue reads that block when present"
            context: |
              Origin: Brian, 2026-09-06 19:40 EDT, tired of remoting into the Windows node.

              Walked with Brian 2026-09-07 11:30 to 11:40 EDT.
            """;

        TaskRecord? read = TaskRecord.TryParse(HandWritten);

        read!.Project.Should().Be("hall9k");
        read.Type.Should().Be("feature");
        read.PreApproval.Should().Be(PreApprovalMode.On, "the boolean spelling a hand-written block uses still reads as the on mode");
        read.Objective.Should().Be("A published task's GitHub issue carries the whole task record");
        read.Criteria.Should().HaveCount(2);
        read.Criteria[0].Should().StartWith("Publishing a task under");
        read.AgentContext.Should().Be(
            "Origin: Brian, 2026-09-06 19:40 EDT, tired of remoting into the Windows node.\n\n"
            + "Walked with Brian 2026-09-07 11:30 to 11:40 EDT.\n");
        read.BlockedByIssues.Should().BeEmpty();
        read.Origin.TaskId.Should().Be(Guid.Empty,
            "the old shape nests the origin, and an origin this build cannot read is absent rather "
            + "than half-invented — the adopting install records no origin at all for it");
    }

    /// <summary>
    /// A hand-annotated block that spaces its criteria out — legal YAML, and the natural way to
    /// write a long contract by hand — used to lose every criterion after the first blank line, so
    /// the adopted draft carried a shorter readiness contract than the origin wrote and nothing
    /// said so (independent pre-PR review, cycle 1, adversarial lens).
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
    public void Dependencies_are_issue_numbers_and_a_hash_prefixed_one_still_reads()
    {
        Sample(blockedBy: [81, 82]).ToYaml().Should().Contain("blocked-by-issues: [81, 82]");
        Sample().ToYaml().Should().Contain("blocked-by-issues: []");

        TaskRecord? read = TaskRecord.TryParse(
            Sample().ToYaml().Replace("blocked-by-issues: []", "blocked-by-issues: [#81, 82]"));

        read!.BlockedByIssues.Should().Equal(81, 82);
    }

    [Fact]
    public void A_dependency_the_record_could_not_name_is_counted_rather_than_dropped()
    {
        TaskRecord? read = TaskRecord.TryParse(Sample(blockedBy: [81], withoutIssues: 2).ToYaml());

        read!.BlockedByIssues.Should().Equal(81);
        read.DependenciesWithoutIssues.Should().Be(
            2, "a blocker with no issue of its own is a fact worth stating, not a silent omission");
    }

    [Fact]
    public void The_epic_travels_as_a_title_beside_the_origins_own_id()
    {
        Guid epicOriginId = Guid.Parse("01a06f2a-0000-7000-8000-00000000000e");

        string yaml = Sample(epicTitle: "Distributed team on the tracker", epicOriginId: epicOriginId).ToYaml();

        yaml.Should().Contain("epic-title: Distributed team on the tracker")
            .And.Contain($"epic-origin-id: {epicOriginId}");
        // Deliberately not the --file "epic:" key: that one resolves as a local id or fragment, and
        // the origin's id names nothing here.
        yaml.Should().NotContain("\nepic: ");
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
        read.Origin.BranchName.Should().Be("task/7325335a-a-published-task-s-github-issu");
        read.Origin.PublishedAt.Should().Be(PublishedAt);
    }

    [Fact]
    public void A_record_with_no_publish_stamp_reads_as_a_time_nobody_observed()
    {
        TaskRecord? read = TaskRecord.TryParse("hall9k-task-record: 1\nobjective: something\n");

        read!.Origin.PublishedAt.Should().Be(DateTimeOffset.MinValue,
            "stamping it with the moment of reading would claim an observation nobody made");
    }

    [Fact]
    public void A_multi_paragraph_agent_context_survives_the_crossing_verbatim()
    {
        const string Context = "First paragraph, with a colon: here.\n\n  An indented second line.\n\nLast.";

        TaskRecord? read = TaskRecord.TryParse(Sample(context: Context).ToYaml());

        read!.AgentContext.Should().Be(Context);
    }
}
