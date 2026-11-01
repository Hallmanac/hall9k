using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>What a person sees of a plan: <c>h9k plan show</c>, the lines <c>h9k task show</c> and every <c>Next:</c> line add, and who a dispatched session may run.</summary>
public sealed class PlanSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _draftId = DomainId.New();
    private readonly Guid _session = DomainId.New();

    private PlanDetails Plan(Action<PlanDraftState>? shape = null, bool live = true)
    {
        PlanDraftState draft = new() { DraftId = _draftId };
        shape?.Invoke(draft);
        return new PlanDetails
        {
            Id = DomainId.New(),
            ProjectId = DomainId.New(),
            State = live ? PlanState.Live : PlanState.Published,
            StartPhase = PlanPhase.Refinement,
            StartedAt = Now,
            Drafts = [draft],
        };
    }

    private IReadOnlyDictionary<Guid, TaskDetails> Tasks(Action<TaskDetails>? shape = null)
    {
        TaskDetails task = new() { Id = _draftId, Objective = "Ship it", State = TaskState.Draft };
        shape?.Invoke(task);
        return new Dictionary<Guid, TaskDetails> { [_draftId] = task };
    }

    private static string Show(
        PlanDetails plan, IReadOnlyDictionary<Guid, TaskDetails> tasks, bool daemon = true, string? gate = null, ProjectDetails? project = null) =>
        string.Join('\n', PlanShowComposer.Compose(plan, project ?? new ProjectDetails { Name = "demo" }, tasks, new PlanShowFacts(daemon, gate), 2));

    [Fact]
    public void A_plan_started_while_the_daemon_is_stopped_says_it_is_waiting_for_it()
    {
        Show(Plan(), Tasks(), daemon: false).Should().Contain("waiting for the daemon").And.Contain("h9k daemon start");
        Show(Plan(), Tasks(), daemon: true).Should().NotContain("waiting for the daemon");
    }

    [Fact]
    public void A_plan_that_starts_nothing_says_which_of_the_three_reasons_holds()
    {
        Show(Plan(), Tasks(), gate: "this node's launch hold stands since 2026-11-01 09:00:00Z: auth")
            .Should().Contain("no session or challenge starts").And.Contain("launch hold");
    }

    [Fact]
    public void Each_draft_prints_its_phase_round_latest_outcome_and_where_its_latest_report_is_from_the_session_id()
    {
        PlanDetails refined = Plan(draft =>
        {
            draft.Phase = PlanPhase.Challenge;
            draft.Round = 1;
            draft.Outcome = PlanOutcome.Revised;
            draft.LatestSessionId = _session;
        });
        string text = Show(refined, Tasks());

        text.Should().Contain("phase: challenge").And.Contain("round 1 of 2").And.Contain("refinement revised the draft")
            .And.Contain(RunPaths.RefinementReportFile(_session));

        PlanDetails challenged = Plan(draft =>
        {
            draft.Phase = PlanPhase.Challenge;
            draft.Round = 2;
            draft.Outcome = PlanOutcome.PublishAsWritten;
            draft.Ready = true;
            draft.LatestSessionId = _session;
            draft.LatestReportIsChallenge = true;
        });
        Show(challenged, Tasks()).Should().Contain("ready to publish").And.Contain("publish as written").And.Contain(RunPaths.ChallengeReportFile(_session));
    }

    [Fact]
    public void A_park_prints_the_draft_the_reason_the_way_forward_and_where_to_read_the_rest()
    {
        PlanDetails parked = Plan(draft =>
        {
            draft.Outcome = PlanOutcome.Question;
            draft.ParkReason = PlanParkReason.Question;
            draft.LatestSessionId = _session;
            draft.LatestReportIsChallenge = false;
        });

        string text = Show(parked, Tasks());

        text.Should().Contain("live, parked").And.Contain(TaskListCommand.ShortId(_draftId)).And.Contain(PlanParkReason.Question.Description)
            .And.Contain(PlanParkReason.Question.WayForward).And.Contain(RunPaths.RefinementReportFile(_session)).And.Contain("journal.md")
            .And.Contain($"h9k plan resume {TaskListCommand.ShortId(parked.Id)}");
    }

    [Fact]
    public void A_refinement_session_after_a_verdict_points_at_its_own_report_and_a_closed_draft_still_shows_its_report()
    {
        Guid challengeSession = DomainId.New();
        PlanDetails afterVerdict = Plan(draft =>
        {
            draft.Outcome = PlanOutcome.DoNotPublish;
            draft.ParkReason = PlanParkReason.RevisionRefused;
            draft.LatestSessionId = _session;
            draft.LatestReportIsChallenge = false;
        });
        string parked = Show(afterVerdict, Tasks());
        parked.Should().Contain(RunPaths.RefinementReportFile(_session)).And.NotContain(RunPaths.ChallengeReportFile(_session));

        PlanDetails published = Plan(draft =>
        {
            draft.Published = true;
            draft.LatestSessionId = challengeSession;
            draft.LatestReportIsChallenge = true;
        });
        Show(published, Tasks(task => task.State = TaskState.Published)).Should().Contain(RunPaths.ChallengeReportFile(challengeSession));
    }

    [Fact]
    public void A_refused_publish_and_a_refused_revision_point_at_the_child_commands_output()
    {
        PlanDetails publish = Plan(draft => draft.ParkReason = PlanParkReason.PublishRefused);
        Show(publish, Tasks()).Should().Contain(RunPaths.PlanPublishOutputFile(publish.Id, _draftId));

        PlanDetails revise = Plan(draft =>
        {
            draft.ParkReason = PlanParkReason.RevisionRefused;
            draft.LatestSessionId = _session;
        });
        Show(revise, Tasks()).Should().Contain(RunPaths.PlanReviseOutputFile(revise.Id, _session));
    }

    [Fact]
    public void After_publishing_a_draft_with_no_tracker_item_and_none_on_its_way_it_says_so_unless_it_was_published_untracked()
    {
        PlanDetails published = Plan(draft => draft.Published = true, live: true);
        ProjectDetails tracking = new() { Name = "demo", BacklogPolicy = BacklogPolicy.Jira };

        Show(published, Tasks(task => task.State = TaskState.Published), project: tracking)
            .Should().Contain("No tracker item").And.Contain(TaskListCommand.ShortId(_draftId));
        Show(published, Tasks(task =>
        {
            task.State = TaskState.Published;
            task.UntrackedAttested = true;
        }), project: tracking).Should().NotContain("No tracker item");
        Show(published, Tasks(task => task.State = TaskState.Published), project: new ProjectDetails { Name = "demo" })
            .Should().NotContain("No tracker item");
    }

    [Fact]
    public void The_attestation_and_who_gave_it_print_on_the_plan()
    {
        PlanDetails plan = Plan();
        plan.NoExistingItemAttested = true;

        Show(plan, Tasks()).Should().Contain("--no-existing-item").And.Contain("the person who started the plan");
    }

    [Fact]
    public void A_drafts_task_show_names_the_live_plan_and_its_phase_and_a_parked_one_says_why()
    {
        PlanDetails plan = Plan(draft =>
        {
            draft.Phase = PlanPhase.Challenge;
            draft.Round = 1;
            draft.ParkReason = PlanParkReason.ChallengeFailed;
        });
        string next = TaskNextStep.PlanStep(TaskListCommand.ShortId(plan.Id));

        next.Should().Contain($"h9k plan show {TaskListCommand.ShortId(plan.Id)}").And.Contain("publishes it");
        TaskDetails draft = new() { Id = _draftId, State = TaskState.Draft, AcceptanceCriteria = ["a"], Objective = "x" };
        TaskNextStep.Lines(draft, hasOpenDependency: false, challengeRequired: true, livePlanId: plan.Id)
            .Should().ContainSingle(line => line.Contains("h9k plan show") && !line.Contains("h9k task publish") && !line.Contains("h9k task challenge"));
        TaskNextStep.Lines(draft, hasOpenDependency: false, challengeRequired: true, livePlanId: null)
            .Should().ContainSingle(line => line.Contains("h9k task challenge"));
    }

    [Fact]
    public void The_refinement_rows_print_on_any_node_with_the_report_path_only_on_the_node_that_ran_it()
    {
        Guid thisNode = DomainId.New();
        Guid otherNode = DomainId.New();
        TaskDetails details = new() { Id = _draftId, State = TaskState.Draft };
        DraftRefinementRecorded recorded = new(
            _draftId, _session, thisNode, RefinementOutcome.Revised, RefinementFailureReason.Unknown, RefinementPurpose.ApplyVerdict,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, Now);
        details.LatestRefinementOutcome = recorded;

        string own = string.Join('\n', TaskShowCommand.RefinementRows(details, thisNode, MemberLabelLookup.Empty).Select(row => row.Markup));
        own.Should().Contain("revised").And.Contain(RunPaths.RefinementReportFile(_session)).And.Contain("apply the verdict's changes");

        string foreign = string.Join('\n', TaskShowCommand.RefinementRows(details, otherNode, MemberLabelLookup.Empty).Select(row => row.Markup));
        foreign.Should().Contain("revised").And.NotContain(RunPaths.RefinementReportFile(_session)).And.Contain("the report is on");

        details.LatestRefinementOutcome = recorded with { Failure = RefinementFailureReason.DraftChanged };
        string notApplied = string.Join('\n', TaskShowCommand.RefinementRows(details, thisNode, MemberLabelLookup.Empty).Select(row => row.Markup));
        notApplied.Should().Contain("not applied").And.Contain(RefinementFailureReason.DraftChanged.Description);

        details.RunningRefinementSessionId = _session;
        details.RunningRefinementNodeId = thisNode;
        details.RunningRefinementDispatchedAt = Now;
        details.RunningRefinementModel = AgentModel.FromInput(AgentModel.ChallengeDefault);
        string running = string.Join('\n', TaskShowCommand.RefinementRows(details, thisNode, MemberLabelLookup.Empty).Select(row => row.Markup));
        running.Should().Contain("running").And.Contain("on this node");
    }

    [Fact]
    public void Show_and_list_are_read_only_to_a_dispatched_session_and_every_write_is_refused()
    {
        IReadOnlyDictionary<Type, (DispatchedSessionAccess Access, string Verb)> classification = DispatchedSessionCommandClassification.BySettingsType;

        classification[typeof(PlanShowCommand.Settings)].Should().Be((DispatchedSessionAccess.ReadOnly, "plan show"));
        classification[typeof(PlanListCommand.Settings)].Should().Be((DispatchedSessionAccess.ReadOnly, "plan list"));
        classification[typeof(PlanStartCommand.Settings)].Should().Be((DispatchedSessionAccess.Refused, "plan start"));
        classification[typeof(PlanResumeCommand.Settings)].Should().Be((DispatchedSessionAccess.Refused, "plan resume"));
        classification[typeof(PlanCancelCommand.Settings)].Should().Be((DispatchedSessionAccess.Refused, "plan cancel"));
    }

    [Fact]
    public void The_refinement_role_is_no_longer_called_future_in_the_config_help()
    {
        foreach (string property in new[] { nameof(ConfigSetCommand.Settings.ModelRefinement), nameof(ConfigSetCommand.Settings.EffortRefinement) })
        {
            string help = typeof(ConfigSetCommand.Settings).GetProperty(property)!
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

            help.Should().NotContain("future").And.Contain("h9k plan start").And.Contain("project");
        }
    }
}
