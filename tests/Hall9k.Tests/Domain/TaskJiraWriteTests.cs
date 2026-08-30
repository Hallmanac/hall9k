using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The compose/execute split's decider gate (Brian's design, 2026-08-28): whether a task is in a
/// position to have hall9k execute a Jira write, and what happens to the pending marker as an
/// attempt succeeds, fails on an expired login, or fails for real. Composition judgment — is the
/// payload a good idea — is never this decider's business; <see cref="JiraWritePayload.Validate"/>
/// covers the guardrail half of that on its own.
/// </summary>
public sealed class TaskJiraWriteTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    private static TaskAggregate Draft(ExternalReference? adopted = null)
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Wire up the Jira write surface", ["It writes a card"],
            TaskType.Feature, agentContext: null, constraints: null, adopted, Now, Owner));
        return task;
    }

    [Fact]
    public void An_unrecognized_operation_is_refused_before_anything_is_recorded()
    {
        // Whatever a caller passes for --op, only create/update/comment resolve — a transition or
        // a close is Unknown, and the executor refuses it regardless of who composed it.
        JiraWriteOperation.FromInput("transition").Should().Be(JiraWriteOperation.Unknown);
        JiraWriteOperation.FromInput("close").Should().Be(JiraWriteOperation.Unknown);

        Action parseTransition = () => JiraWriteOperation.Parse("transition");
        parseTransition.Should().Throw<DomainValidationException>().WithMessage("*transition or a close is refused*");
    }

    [Fact]
    public void A_field_that_moves_workflow_state_is_refused_however_it_is_labelled()
    {
        JiraWritePayload payload = new(
            WorkItemType: "Dev Task", Fields: new Dictionary<string, string> { ["status"] = "Done" }, Comment: null);

        Action validate = () => payload.Validate(JiraWriteOperation.Update);

        validate.Should().Throw<DomainValidationException>().WithMessage("*refuses a transition or a close*");
    }

    [Fact]
    public void A_create_needs_a_work_item_type_because_hall9k_models_nothing_about_it()
    {
        JiraWritePayload payload = new(WorkItemType: null, Fields: null, Comment: null);

        Action validate = () => payload.Validate(JiraWriteOperation.Create);

        validate.Should().Throw<DomainValidationException>().WithMessage("*needs a work item type*");
    }

    [Fact]
    public void A_create_needs_a_summary_field_because_twg_itself_refuses_without_one()
    {
        JiraWritePayload payload = new(WorkItemType: "Dev Task", Fields: null, Comment: null);

        Action validate = () => payload.Validate(JiraWriteOperation.Create);

        validate.Should().Throw<DomainValidationException>().WithMessage("*needs a \"summary\" field*");
    }

    [Fact]
    public void An_update_with_no_fields_is_refused_because_it_would_change_nothing()
    {
        JiraWritePayload payload = new(WorkItemType: null, Fields: null, Comment: null);

        Action validate = () => payload.Validate(JiraWriteOperation.Update);

        validate.Should().Throw<DomainValidationException>().WithMessage("*would change nothing*");
    }

    [Fact]
    public void An_update_whose_only_field_is_blank_is_refused_the_same_as_no_fields_at_all()
    {
        JiraWritePayload payload = JiraWritePayload.FromJson("""{"fields":{"summary":""}}""");

        Action validate = () => payload.Validate(JiraWriteOperation.Update);

        validate.Should().Throw<DomainValidationException>().WithMessage("*would change nothing*");
    }

    /// <summary>
    /// A field composed through <see cref="JiraWritePayload.FromJson"/> is stored as its own raw
    /// JSON text, so an empty summary arrives here as the two-character string <c>""</c> rather
    /// than an empty one — this validation has to decode it the same way
    /// <see cref="Hall9k.Connectors.WorkItems.TwgJiraExecutor"/>'s own field extraction does before
    /// deciding whether it is blank (independent pre-PR review, cycle 7), or an empty summary
    /// passes this gate only for twg to refuse it after the intent is already recorded.
    /// </summary>
    [Fact]
    public void A_json_composed_blank_summary_is_refused_the_same_as_a_missing_one()
    {
        JiraWritePayload payload = JiraWritePayload.FromJson(
            """{"workItemType":"Dev Task","fields":{"summary":"","description":"Fixes the thing"}}""");

        Action validate = () => payload.Validate(JiraWriteOperation.Create);

        validate.Should().Throw<DomainValidationException>().WithMessage("*needs a \"summary\" field*");
    }

    [Fact]
    public void An_unrecognized_text_format_is_refused_before_anything_is_recorded()
    {
        JiraWritePayload payload = new(
            WorkItemType: "Dev Task",
            Fields: new Dictionary<string, string> { ["summary"] = "A card" },
            Comment: null,
            Format: "wiki");

        Action validate = () => payload.Validate(JiraWriteOperation.Create);

        validate.Should().Throw<DomainValidationException>().WithMessage("*not a text format twg accepts*");
    }

    [Fact]
    public void A_payload_naming_no_format_defaults_to_markdown()
    {
        JiraWritePayload payload = new(WorkItemType: "Dev Task", Fields: null, Comment: null);

        payload.EffectiveFormat.Should().Be("markdown");
    }

    [Fact]
    public void A_payloads_named_format_round_trips_through_json()
    {
        JiraWritePayload payload = new(WorkItemType: "Dev Task", Fields: null, Comment: "note", Format: "plain");

        JiraWritePayload roundTripped = JiraWritePayload.FromJson(payload.ToJson());

        roundTripped.EffectiveFormat.Should().Be("plain");
    }

    /// <summary>
    /// The intent recorded on <see cref="Events.JiraWriteRequested"/> is the payload exactly as
    /// submitted — never a derived reading of it. <see cref="JiraWritePayload.EffectiveFormat"/>
    /// is computed from <c>Format</c>, not part of what was composed, so it must not leak into the
    /// serialized audit record even though it is a public property (independent pre-PR review,
    /// cycle 3).
    /// </summary>
    [Fact]
    public void The_serialized_payload_never_carries_the_computed_effective_format()
    {
        JiraWritePayload payload = new(WorkItemType: "Dev Task", Fields: null, Comment: null);

        payload.ToJson().Should().NotContain("effectiveFormat", "EffectiveFormat is derived, not submitted, and must not appear in the recorded intent");
    }

    [Fact]
    public void A_create_is_requested_with_no_target_key_because_it_has_none_yet()
    {
        TaskAggregate task = Draft();

        JiraWriteRequested requested = TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Create, issueKey: null, "{}", DomainId.New(), Now, Owner);

        requested.Operation.Should().Be(JiraWriteOperation.Create);
        requested.IssueKey.Should().BeNull();
    }

    [Fact]
    public void A_create_on_a_task_already_linked_is_refused_the_same_way_publication_is()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));

        Action create = () => TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Create, null, "{}", DomainId.New(), Now, Owner);

        create.Should().Throw<DomainConflictException>().WithMessage("*already linked to jira:PROJ-123*");
    }

    [Fact]
    public void An_update_resolves_its_target_from_the_tasks_own_linked_item_when_none_is_named()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));

        JiraWriteRequested requested = TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Update, issueKey: null, "{}", DomainId.New(), Now, Owner);

        requested.IssueKey.Should().Be("PROJ-123", "the event is the complete record of what was requested");
    }

    [Fact]
    public void An_update_with_no_linked_item_and_no_explicit_key_is_refused()
    {
        TaskAggregate task = Draft();

        Action update = () => TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Update, issueKey: null, "{}", DomainId.New(), Now, Owner);

        update.Should().Throw<DomainValidationException>().WithMessage("*carries no linked Jira item*");
    }

    [Fact]
    public void A_write_is_refused_against_a_task_a_human_has_abandoned()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));
        task.Apply(new TaskAbandoned(task.Id, "work no longer needed", Now, Owner));

        Action write = () => TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Comment, null, "{}", DomainId.New(), Now, Owner);

        write.Should().Throw<DomainConflictException>().WithMessage("*was abandoned*");
    }

    [Fact]
    public void A_second_write_is_refused_while_one_is_outstanding()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));
        task.Apply(TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Comment, null, "{}", DomainId.New(), Now, Owner));

        Action again = () => TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Comment, null, "{}", DomainId.New(), Now, Owner);

        again.Should().Throw<DomainConflictException>().WithMessage("*already has a Jira write outstanding*");
    }

    [Fact]
    public void An_auth_failure_keeps_the_write_pending_so_a_retry_can_finish_it()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));
        Guid writeId = DomainId.New();
        task.Apply(TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Comment, null, "{\"comment\":\"merged\"}", writeId, Now, Owner));

        task.Apply(TaskDecider.RecordJiraWriteFailure(
            task, writeId, "twg is not authenticated", isAuthFailure: true, Now));

        task.PendingJiraWriteId.Should().Be(writeId, "the identical payload is what a retry re-attempts");
        task.PendingJiraWriteIsAuthFailure.Should().BeTrue();
        task.PendingJiraWriteFailureReason.Should().Be("twg is not authenticated");
        task.PendingJiraWritePayloadJson.Should().Be("{\"comment\":\"merged\"}", "nothing here is recomposed");
    }

    [Fact]
    public void A_non_auth_failure_ends_the_write_because_the_identical_payload_would_fail_again()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));
        Guid writeId = DomainId.New();
        task.Apply(TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Comment, null, "{}", writeId, Now, Owner));

        task.Apply(TaskDecider.RecordJiraWriteFailure(task, writeId, "Jira refused: field required", isAuthFailure: false, Now));

        task.PendingJiraWriteId.Should().BeNull();
        task.PendingJiraWriteIsAuthFailure.Should().BeFalse();
        task.PendingJiraWriteFailureReason.Should().BeNull();
    }

    [Fact]
    public void A_success_clears_the_pending_marker()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));
        Guid writeId = DomainId.New();
        task.Apply(TaskDecider.RequestJiraWrite(
            task, JiraWriteOperation.Comment, null, "{}", writeId, Now, Owner));

        task.Apply(TaskDecider.RecordJiraWriteSuccess(task, writeId, "PROJ-123", "twg reported it", Now));

        task.PendingJiraWriteId.Should().BeNull();
    }

    [Fact]
    public void Recording_an_outcome_for_a_write_that_is_not_pending_is_refused()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));

        Action record = () => TaskDecider.RecordJiraWriteSuccess(task, DomainId.New(), "PROJ-123", "s", Now);

        record.Should().Throw<DomainConflictException>().WithMessage("*no outstanding Jira write*");
    }

    [Fact]
    public void The_projections_carry_the_pending_auth_failure_the_attention_pane_reads()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.Jira, "PROJ-123"), Now, Owner);
        Guid writeId = DomainId.New();
        JiraWriteRequested requested = new(
            added.Id, writeId, JiraWriteOperation.Comment, "PROJ-123", "{}", Owner, Now);
        JiraWriteFailed failed = new(added.Id, writeId, "twg is not authenticated", true, Now);

        TaskDetailsProjection details = new();
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(added));
        details.Apply(new FakeEvent<JiraWriteRequested>(requested), detail);
        details.Apply(new FakeEvent<JiraWriteFailed>(failed), detail);

        detail.PendingJiraWriteId.Should().Be(writeId);
        detail.PendingJiraWriteIsAuthFailure.Should().BeTrue();
        detail.PendingJiraWriteFailureReason.Should().Be("twg is not authenticated");

        TaskListItemProjection list = new();
        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(added));
        list.Apply(new FakeEvent<JiraWriteRequested>(requested), row);
        list.Apply(new FakeEvent<JiraWriteFailed>(failed), row);

        row.PendingJiraWriteIsAuthFailure.Should().BeTrue();
        row.PendingJiraWriteFailureReason.Should().Be("twg is not authenticated");
    }

    [Fact]
    public void A_terminal_failure_clears_the_row_so_the_pane_stops_asking_for_a_login()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.Jira, "PROJ-123"), Now, Owner);
        Guid writeId = DomainId.New();

        TaskListItemProjection list = new();
        TaskListItem row = list.Create(new FakeEvent<TaskAdded>(added));
        list.Apply(
            new FakeEvent<JiraWriteRequested>(new JiraWriteRequested(added.Id, writeId, JiraWriteOperation.Comment, "PROJ-123", "{}", Owner, Now)),
            row);
        list.Apply(
            new FakeEvent<JiraWriteFailed>(new JiraWriteFailed(added.Id, writeId, "field required", false, Now)),
            row);

        row.PendingJiraWriteIsAuthFailure.Should().BeFalse();
        row.PendingJiraWriteFailureReason.Should().BeNull();
    }

    [Fact]
    public void A_merge_notice_queued_behind_another_write_is_drained_exactly_once()
    {
        TaskAggregate task = Draft(new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));

        task.Apply(TaskDecider.QueueJiraMergeNotice(task, Now));
        task.HasQueuedJiraMergeNotice.Should().BeTrue();

        Action queueAgain = () => TaskDecider.QueueJiraMergeNotice(task, Now);
        queueAgain.Should().Throw<DomainConflictException>().WithMessage("*already has a merge notice queued*");

        task.Apply(TaskDecider.RecordJiraMergeNoticeAttempted(task, Now));
        task.HasQueuedJiraMergeNotice.Should().BeFalse();

        Action attemptAgain = () => TaskDecider.RecordJiraMergeNoticeAttempted(task, Now);
        attemptAgain.Should().Throw<DomainConflictException>().WithMessage("*no queued merge notice*");
    }

    [Fact]
    public void Abandoning_a_task_drops_its_queued_merge_notice_on_the_aggregate_and_the_projection()
    {
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Objective", ["A criterion"], TaskType.Feature,
            agentContext: null, constraints: null,
            externalReference: new ExternalReference(WorkItemProvider.Jira, "PROJ-123"), Now, Owner);

        TaskAggregate task = new();
        task.Apply(added);
        task.Apply(TaskDecider.QueueJiraMergeNotice(task, Now));
        task.Apply(new TaskAbandoned(added.Id, "work no longer needed", Now, Owner));

        task.HasQueuedJiraMergeNotice.Should().BeFalse(
            "nothing is still owed once a human has walked away from the task");

        TaskDetailsProjection details = new();
        TaskDetails detail = details.Create(new FakeEvent<TaskAdded>(added));
        details.Apply(new FakeEvent<JiraMergeNoticeQueued>(new JiraMergeNoticeQueued(added.Id, Now)), detail);
        details.Apply(new FakeEvent<TaskAbandoned>(new TaskAbandoned(added.Id, "work no longer needed", Now, Owner)), detail);

        detail.HasQueuedJiraMergeNotice.Should().BeFalse(
            "this view is what the retry sweep queries to decide whether to drain a notice");
    }
}
