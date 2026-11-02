using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>h9k plan start --idea</c>'s refusals over the facts alone, and what <c>h9k plan show</c> and <c>h9k idea show</c> say of a
/// plan started from an idea, including a report that is missing or unreadable.
/// </summary>
public sealed class PlanFromIdeaSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _owner = DomainId.New();
    private readonly Guid _projectId = DomainId.New();
    private readonly Guid _ideaId = DomainId.New();

    private ProjectDetails Project(bool skip = true, bool archived = false, DateTimeOffset? purgeAt = null) => new()
    {
        Id = _projectId,
        Name = "hall9k",
        SkipPermissions = skip,
        IsArchived = archived,
        PurgeAt = purgeAt,
    };

    private IdeaAggregate Idea(bool linked = false, bool assignedToProject = true, bool concluded = false)
    {
        IdeaAggregate idea = new();
        idea.Apply(new IdeaCaptured(_ideaId, _owner, "Let a plan start from an idea", assignedToProject ? _projectId : null, Now));
        if (linked)
        {
            idea.Apply(IdeaDecider.LinkWorkItem(_ideaId, ExternalReference.Parse("github:acme/api#7"), "A title", "open", Now, Now, _owner));
        }

        if (concluded)
        {
            idea.Apply(IdeaDecider.Conclude(idea, "done", Now, _owner));
        }

        return idea;
    }

    private PlanStarted Decide(
        IdeaAggregate idea, bool mayAct = true, ProjectDetails? project = null, bool noProject = false, IReadOnlyList<PlanDetails>? live = null,
        bool workspace = true, string? missingCheckout = null) =>
        PlanStartRules.DecideFromIdea(
            idea, mayAct, noProject ? null : project ?? Project(), live ?? [], workspace, missingCheckout, _owner, Now);

    [Fact]
    public void A_capturable_idea_in_a_plain_project_records_a_plan_in_discovery_with_no_drafts_and_no_attestation()
    {
        PlanStarted started = Decide(Idea());

        (started.IdeaId, started.StartPhase, started.DraftIds, started.ProjectId, started.StartedByOwnerId)
            .Should().Be((_ideaId, PlanPhase.Discovery, started.DraftIds, _projectId, _owner));
        started.DraftIds.Should().BeEmpty();
        (started.NoExistingItemAttested, started.UntrackedAttested).Should().Be((false, false));
    }

    [Fact]
    public void Anyone_but_the_holder_is_refused_in_this_commands_own_words_with_no_override_offered()
    {
        Action act = () => Decide(Idea(), mayAct: false);

        string message = act.Should().Throw<DomainBusinessRuleException>().Which.Message;
        message.Should().Contain("assignee").And.Contain("creator when nobody holds it").And.Contain("no override");
        message.Should().NotContain("--holder").And.NotContain("--reason");
    }

    [Fact]
    public void An_idea_that_is_not_captured_has_no_project_or_workspace_here_or_is_already_in_a_live_plan_is_refused_with_the_way_forward()
    {
        Action concluded = () => Decide(Idea(concluded: true));
        concluded.Should().Throw<DomainException>().Which.Message.Should().Contain("concluded").And.Contain("start a plan on");

        Action noProjectOnIdea = () => Decide(Idea(assignedToProject: false));
        noProjectOnIdea.Should().Throw<DomainValidationException>().Which.Message.Should().Contain("h9k idea move");

        Action projectNotHeld = () => Decide(Idea(), noProject: true);
        projectNotHeld.Should().Throw<DomainNotFoundException>().Which.Message.Should().Contain("does not hold");

        Action noWorkspace = () => Decide(Idea(), workspace: false);
        noWorkspace.Should().Throw<DomainBusinessRuleException>().Which.Message.Should().Contain("Start the plan on the node that captured the idea");

        PlanDetails existing = new()
        {
            Id = DomainId.New(), State = PlanState.Live, StartPhase = PlanPhase.Discovery, Discovery = new PlanDiscoveryState { IdeaId = _ideaId },
        };
        Action alreadyLive = () => Decide(Idea(), live: [existing]);
        alreadyLive.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("already in plan").And.Contain("h9k plan cancel");

        // Not live, so it does not stand in the way of another look.
        PlanDetails ended = new()
        {
            Id = DomainId.New(), State = PlanState.Stopped, StartPhase = PlanPhase.Discovery, Discovery = new PlanDiscoveryState { IdeaId = _ideaId },
        };
        Decide(Idea(), live: [ended]).IdeaId.Should().Be(_ideaId);
    }

    [Fact]
    public void A_project_that_is_archived_scheduled_for_purge_without_a_checkout_or_without_skip_permissions_is_refused()
    {
        Action archived = () => Decide(Idea(), project: Project(archived: true));
        archived.Should().Throw<DomainConflictException>().Which.Message.Should().Contain("archived");

        Action purge = () => Decide(Idea(), project: Project(purgeAt: Now.AddDays(3)));
        purge.Should().Throw<DomainValidationException>().Which.Message.Should().Contain("permanent deletion").And.Contain("cancel-purge");

        Action noCheckout = () => Decide(Idea(), missingCheckout: "/nowhere/dev");
        noCheckout.Should().Throw<DomainBusinessRuleException>().Which.Message.Should().Contain("/nowhere/dev");

        Action noSkip = () => Decide(Idea(), project: Project(skip: false));
        noSkip.Should().Throw<DomainBusinessRuleException>().Which.Message.Should().Contain("skip-permissions");
    }

    [Fact]
    public void An_idea_that_carries_a_tracker_link_is_refused_whatever_its_note_says_and_the_refusal_names_the_way_forward()
    {
        IdeaAggregate linked = Idea(linked: true);
        linked.Apply(new IdeaRevised(_ideaId, "A note a person rewrote entirely in their own words", Now, _owner));

        Action act = () => Decide(linked);

        string message = act.Should().Throw<DomainValidationException>().Which.Message;
        message.Should().Contain("github:acme/api#7").And.Contain("h9k task add --from-idea").And.Contain("h9k task revise <draft> --context")
            .And.Contain("h9k plan start <draft>");
    }

    private PlanDetails PlanAt(Action<PlanDetails>? shape = null)
    {
        PlanDetails plan = new()
        {
            Id = DomainId.New(),
            ProjectId = _projectId,
            State = PlanState.Live,
            StartPhase = PlanPhase.Discovery,
            StartedAt = Now,
            Discovery = new PlanDiscoveryState { IdeaId = _ideaId, ParkReason = PlanParkReason.Gate, LatestSessionId = DomainId.New() },
        };
        shape?.Invoke(plan);
        return plan;
    }

    private static string Show(PlanDetails plan, DiscoveryShowFacts? facts) =>
        string.Join('\n', PlanShowComposer.Compose(
            plan, new ProjectDetails { Name = "demo" }, new Dictionary<Guid, TaskDetails>(), new PlanShowFacts(true, null, facts), 2));

    [Fact]
    public void At_the_gate_plan_show_names_the_idea_says_what_the_report_says_lists_the_objectives_and_prints_the_path_and_the_resume_line()
    {
        DiscoveryReadout readout = DiscoveryReport.TryRead("DISCOVERY: pursue\nTASK: Build the first piece\nTASK: Build the second piece\n")!;
        DiscoveryShowFacts facts = new(
            "Let a plan start from an idea", "/ws/discovery-abcd1234.md", new DiscoveryReading(false, readout), "cd \"/co\" && claude --resume abc");

        string text = Show(PlanAt(), facts);

        text.Should().Contain($"idea: {DomainId.Short(_ideaId)}").And.Contain("Let a plan start from an idea").And.Contain("phase: discovery")
            .And.Contain("parked at discovery's gate").And.Contain(PlanParkReason.Gate.Description)
            .And.Contain("the report says [green]pursue[/], proposing 2 tasks").And.Contain("Build the first piece").And.Contain("Build the second piece")
            .And.Contain("path: /ws/discovery-abcd1234.md").And.Contain("claude --resume abc")
            .And.Contain("h9k plan pursue").And.Contain("h9k plan stop").And.Contain("h9k plan resume is refused here")
            .And.Contain("drafts: none yet");
    }

    [Fact]
    public void A_report_that_says_stop_a_missing_report_and_an_unreadable_one_are_each_said_and_the_path_and_the_line_still_print()
    {
        const string line = "cd \"/co\" && claude --resume abc";

        Show(PlanAt(), new DiscoveryShowFacts("n", "/ws/r.md", new DiscoveryReading(false, DiscoveryReport.TryRead("DISCOVERY: stop")), line))
            .Should().Contain("the report says [yellow]stop[/]").And.Contain("path: /ws/r.md").And.Contain(line);
        Show(PlanAt(), new DiscoveryShowFacts("n", "/ws/r.md", new DiscoveryReading(true, null), line))
            .Should().Contain("the report is not there").And.Contain("path: /ws/r.md").And.Contain(line);
        Show(PlanAt(), new DiscoveryShowFacts("n", "/ws/r.md", new DiscoveryReading(false, null), line))
            .Should().Contain("not readable").And.Contain("DISCOVERY:").And.Contain("path: /ws/r.md").And.Contain(line);
    }

    [Fact]
    public void A_report_is_printed_through_external_text_so_markup_in_an_objective_cannot_style_the_terminal()
    {
        DiscoveryReadout readout = DiscoveryReport.TryRead("DISCOVERY: pursue\nTASK: [red]Fake[/] [bold]markup\n")!;

        Show(PlanAt(), new DiscoveryShowFacts("n", "/ws/r.md", new DiscoveryReading(false, readout), null))
            .Should().Contain("[[red]]Fake[[/]]");
    }

    [Fact]
    public void A_failed_session_a_running_one_and_a_stopped_plan_each_read_as_what_they_are()
    {
        string failed = Show(
            PlanAt(plan => plan.Discovery = new PlanDiscoveryState
            {
                IdeaId = _ideaId, ParkReason = PlanParkReason.SessionFailed, LatestSessionId = DomainId.New(),
            }),
            null);
        failed.Should().Contain("parked:").And.Contain(PlanParkReason.SessionFailed.Description).And.Contain("h9k plan resume").And.NotContain("h9k plan pursue");

        string running = Show(
            PlanAt(plan => plan.Discovery = new PlanDiscoveryState { IdeaId = _ideaId, RunningSessionId = DomainId.New() }), null);
        running.Should().Contain("a discovery session is running").And.Contain("discovery-report.md");

        string stopped = Show(
            PlanAt(plan =>
            {
                plan.State = PlanState.Stopped;
                plan.EndedAt = Now;
                plan.StopReason = "an exploration";
            }),
            new DiscoveryShowFacts("n", "/ws/r.md", new DiscoveryReading(false, DiscoveryReport.TryRead("DISCOVERY: stop")), null));
        stopped.Should().Contain("stopped").And.Contain("a normal ending").And.Contain("reason given: an exploration").And.NotContain("cancelled");
    }

    [Fact]
    public void The_resume_line_is_a_cd_to_the_checkout_and_claude_resume_on_the_session_id_without_skipping_permissions()
    {
        Guid session = DomainId.New();

        string posix = LaunchTextDefaults.DiscoveryResumeLine("/home/me/proj\"x", session, LaunchShell.Posix);
        string powershell = LaunchTextDefaults.DiscoveryResumeLine("C:\\proj", session, LaunchShell.PowerShell);

        posix.Should().Be($"cd \"/home/me/proj\\\"x\" && claude --resume {session}");
        powershell.Should().Be($"cd \"C:\\proj\"; claude --resume {session}");
        posix.Should().NotContain("dangerously-skip-permissions");
    }

    [Fact]
    public void Idea_show_names_the_newest_plan_with_its_state_and_phase_and_at_the_gate_the_two_answers()
    {
        PlanDetails gate = PlanAt();
        string shortId = DomainId.Short(gate.Id);

        IdeaShowCommand.PlanLine(gate).Should().Contain(shortId).And.Contain("live").And.Contain("discovery")
            .And.Contain($"h9k plan pursue {shortId}").And.Contain($"h9k plan stop {shortId}");
        PlanDetails running = PlanAt(plan => plan.Discovery = new PlanDiscoveryState { IdeaId = _ideaId });
        IdeaShowCommand.PlanLine(running)
            .Should().Contain($"h9k plan show {DomainId.Short(running.Id)}").And.NotContain("h9k plan pursue");
    }
}
