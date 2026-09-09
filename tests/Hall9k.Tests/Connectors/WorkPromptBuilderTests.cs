using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The delivery-arrangement sentences <see cref="WorkPromptBuilder.Build"/> can hand a session,
/// one per launch shape: a dispatcher-launched headless build (watched by RunSupervisor), an
/// operator's attached <c>h9k task work</c> claim — with or without the prompt-handoff model's
/// own self-registration content (R4, idea fcaded0b's design rulings, Take the Wheel epic
/// 9272e514's slice 7): the default <c>h9k task work</c> path sets <c>requiresSelfRegistration</c>,
/// and the kept-for-one-release <c>--direct-launch</c> path does not, since that path still
/// records the session itself the way it always did — and a deliberate <c>h9k task start</c>
/// kick-off: headless like the first, and — since <c>RunSupervisor.AdoptDeliberateHeadlessStartsAsync</c>
/// (task: a do-now session launched by h9k task start is caught within seconds) — watched too,
/// but never told to trigger verification or delivery itself, unlike the first shape (independent
/// pre-PR review, cycle 1, both lenses: the session was previously told nothing supervised it at
/// all, which stopped being true the moment that sweep started adopting it).
/// </summary>
public sealed class WorkPromptBuilderTests
{
    private readonly string _worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-work-prompt-{Guid.NewGuid():N}");

    [Fact]
    public void A_dispatcher_launched_build_is_told_the_platform_verifies_and_opens_the_pr()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: false);

        prompt.Should().Contain("the platform verifies and opens the PR after you finish.");
        prompt.Should().NotContain("nothing supervises this run");
        prompt.Should().NotContain("delivery is `h9k task deliver`, run by the operator explicitly");
    }

    [Fact]
    public void An_attended_h9k_task_work_session_is_told_delivery_is_its_own_explicit_command()
    {
        string prompt = Build(isInteractive: true, isDeliberateHeadlessStart: false);

        prompt.Should().Contain("delivery is `h9k task deliver`, run by the operator explicitly");
        prompt.Should().NotContain("the platform verifies and opens the PR after you finish.");
        prompt.Should().NotContain("nothing supervises this run");
    }

    /// <summary>
    /// h9k task start's own claim carries the ceiling-exempt sentinel Guid.Empty NodeId, so the
    /// ordinary NodeId == nodeId sweeps never adopt it — but RunSupervisor.AdoptDeliberateHeadlessStartsAsync
    /// (task: a do-now session launched by h9k task start is caught within seconds) does, widened
    /// on DispatchingNodeId instead, and delivers a clean, committed tree automatically or flags
    /// anything else for a human the moment the session exits, so the prompt must say that rather
    /// than claiming nothing watches this run at all.
    /// </summary>
    [Fact]
    public void A_deliberate_headless_start_is_told_the_platform_delivers_or_flags_it_automatically()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: true);

        prompt.Should().Contain("the platform checks the worktree itself");
        prompt.Should().Contain("delivered automatically");
        prompt.Should().Contain("flagged for a human instead");
        prompt.Should().NotContain("nothing supervises this run");
        prompt.Should().NotContain("the platform verifies and opens the PR after you finish.");
        prompt.Should().NotContain("delivery is `h9k task deliver`, run by the operator explicitly");
    }

    /// <summary>
    /// h9k task deliver and h9k task verify both refuse unconditionally when invoked from inside
    /// the very session that holds the claim they would act on (InteractiveSessionLiveness.
    /// EnsureNotAttachedElsewhere finds its own recorded pid alive; verify's self-invocation
    /// exemption keys on HALL9K_INTERACTIVE_RUN_ID, which HeadlessLaunch.SpawnDetached never
    /// sets) — so the prompt must not tell this session to trigger either itself (independent
    /// pre-PR review, cycle 4, both lenses).
    /// </summary>
    [Fact]
    public void A_deliberate_headless_start_is_told_a_human_not_itself_triggers_verify_and_delivery()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: true);

        prompt.Should().Contain("Verification and delivery are still not yours to trigger");
        prompt.Should().Contain("do not attempt them yourself");
        prompt.Should().NotContain("yours to trigger by hand once you finish");
    }

    [Fact]
    public void Interactive_prompt_without_self_registration_carries_none_of_its_content()
    {
        string prompt = Build(isInteractive: true, isDeliberateHeadlessStart: false);

        prompt.Should().NotContain("register-session", "--direct-launch keeps the launch-time recording it always had");
        prompt.Should().NotContain("slice-1 names");
        prompt.Should().Contain("h9k task deliver", "delivery stays explicit regardless of which launch path got here");
    }

    [Fact]
    public void Interactive_prompt_with_self_registration_tells_the_session_to_register_itself()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain($"h9k task register-session {task.Id}");
        prompt.Should().Contain("did not launch you");
    }

    [Fact]
    public void Interactive_prompt_with_self_registration_points_at_slice_1_names_and_task_show()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain("slice-1 names");
        prompt.Should().Contain($"h9k task show {task.Id}");
    }

    [Fact]
    public void Self_registration_content_never_leaks_into_a_headless_dispatch_prompt()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: false);

        prompt.Should().NotContain("register-session");
        prompt.Should().NotContain("slice-1 names");
    }

    [Fact]
    public void Interactive_prompt_with_self_registration_carries_the_worktree_path_rather_than_asserting_it()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain(_worktreePath, "the prompt was pasted into a session that may not already be there");
        prompt.Should().NotContain(
            "You are in an isolated git worktree",
            "that claim is false unless the operator's session happens to already be running there");
    }

    [Fact]
    public void Interactive_prompt_without_self_registration_still_asserts_it_is_in_the_worktree()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true);

        prompt.Should().Contain(
            "You are in an isolated git worktree", "--direct-launch sets the child process's own working directory to it");
    }

    [Fact]
    public void Interactive_prompt_still_states_delivery_is_explicit_and_never_the_sessions_own_call()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain("run by the operator explicitly");
        prompt.Should().Contain("nothing pushes or");
        prompt.Should().Contain(
            "only once the",
            "the self-delivery bullet later in the prompt must not read as unqualified permission to deliver unprompted");
        prompt.Should().Contain("your own unprompted call");
    }

    [Fact]
    public void Interactive_prompt_tells_a_self_delivering_session_to_pass_handoff_and_stop_afterward()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain("--handoff", "the operator-facing handoff prompt can never reach a Bash tool call");
        prompt.Should().Contain("stop working in this worktree");
    }

    [Fact]
    public void Direct_launch_prompt_also_carries_the_self_delivery_rule()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true);

        prompt.Should().Contain(
            "stop working in this worktree",
            "IsSelfInvocation's own CLAUDE_PID/InteractiveRunEnvironmentVariable exemption applies to a "
            + "direct-launch child too, not only a self-registered session");
    }

    [Fact]
    public void Only_self_registration_restates_the_co_author_and_timeout_invariants()
    {
        TaskDetails task = SomeTask();

        string withSelfRegistration = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);
        string withoutSelfRegistration = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true);

        withSelfRegistration.Should().Contain("Co-Authored-By");
        withoutSelfRegistration.Should().NotContain(
            "Co-Authored-By", "--direct-launch always passes --settings itself, so nothing here can be skipped");
    }

    [Fact]
    public void Timeout_reminder_names_this_projects_own_gates_rather_than_a_hardcoded_dotnet_fact()
    {
        TaskDetails task = SomeTask();
        ProjectDetails nodeProject = SomeProject();
        nodeProject.VerifyCommands.Add(new VerifyCommand("test", "npm test"));

        string prompt = WorkPromptBuilder.Build(
            task, nodeProject, "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain("`npm test`");
        prompt.Should().NotContain("dotnet test", "this project configures no such gate");
    }

    [Fact]
    public void Timeout_reminder_names_no_gate_honestly_when_the_project_configures_none()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true, requiresSelfRegistration: true);

        prompt.Should().Contain("configures no verification gates");
        prompt.Should().NotContain("dotnet test");
    }

    /// <summary>
    /// h9k task delegate's own framing (design ruling R6, idea fcaded0b's design rulings, Take the
    /// Wheel epic 9272e514's slice 10): distinct from both the handback branch and the causeless
    /// "a previous attempt worked here first" branch, since a delegated contractor always has a
    /// real, current author to name.
    /// </summary>
    [Fact]
    public void A_delegated_contractor_is_told_a_human_dispatched_it_while_staying_the_arbiter()
    {
        TaskDetails task = SomeTask();

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: true,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Drafted the migration.");

        prompt.Should().Contain("A human delegated this phase to you");
        prompt.Should().Contain("h9k task delegate");
        prompt.Should().Contain("still in interactive mode");
        prompt.Should().Contain("h9k task work` to continue by hand");
        prompt.Should().NotContain("A previous attempt worked here first");
        prompt.Should().NotContain("A human began this work interactively");
    }

    [Fact]
    public void A_delegated_contractors_note_is_quoted_verbatim_in_its_starting_prompt()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: false,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true,
            delegationNote: "Attempted: the retry loop. Deliberate: left the timeout at 30s. Latitude: rewrite tests freely.");

        prompt.Should().Contain("Their handoff note, verbatim:");
        prompt.Should().Contain("> Attempted: the retry loop. Deliberate: left the timeout at 30s. Latitude: rewrite tests freely.");
    }

    /// <summary>
    /// Design ruling R6's own closing line: "the prompt's default for inherited work stays
    /// conservative" — stated unconditionally so a contractor never infers discard latitude on its
    /// own, regardless of whether this delegation is the claim's first or a later one.
    /// </summary>
    [Fact]
    public void A_delegated_contractor_is_told_to_respect_inherited_work_by_default()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: true,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Just started.");

        prompt.Should().Contain("Respect what is already here by default");
        prompt.Should().Contain("latitude the operator grants");
    }

    [Fact]
    public void A_delegated_contractor_onto_a_virgin_branch_is_told_the_worktree_is_clean()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: false,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Nothing yet.");

        prompt.Should().Contain("Nothing has been committed on this branch yet");
        prompt.Should().NotContain("This worktree already holds work");
    }

    [Fact]
    public void A_delegated_contractor_onto_a_non_virgin_branch_is_told_to_review_what_is_there()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: true,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Picking up midway.");

        prompt.Should().Contain("This worktree already holds work");
        prompt.Should().Contain("git status");
        prompt.Should().NotContain("Nothing has been committed on this branch yet");
    }

    /// <summary>
    /// A delegated contractor still runs unsupervised and unattended exactly like a start-it-mine
    /// session — nothing about the delegated framing changes how delivery, checkpoints, or the
    /// self-review phase are described.
    /// </summary>
    [Fact]
    public void A_delegated_contractor_still_gets_the_deliberate_headless_start_working_rules()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: false,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Nothing yet.");

        prompt.Should().Contain("nothing supervises this run");
        prompt.Should().Contain("a human's to trigger by hand");
    }

    /// <summary>
    /// When the contractor's own base commit could not be read (git was unreadable in the claim's
    /// worktree at dispatch time), the reset/recompose step has no safe boundary and is skipped —
    /// but the self-review phase and the project's own verification gates do not depend on that
    /// boundary, and dropping them silently handed back ungated, unreviewed work (conformance and
    /// adversarial review, cycle 1).
    /// </summary>
    [Fact]
    public void A_delegated_contractor_with_no_readable_base_commit_still_gets_self_review_and_gates()
    {
        ProjectDetails project = SomeProject();
        project.VerifyCommands.Add(new VerifyCommand("test", "dotnet test"));

        string prompt = WorkPromptBuilder.Build(
            SomeTask(), project, "task/1-slug", _worktreePath, resumesPreviousWork: true,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Picking up midway.",
            delegationBaseCommit: null);

        prompt.Should().Contain("Self-review phase");
        prompt.Should().Contain("`dotnet test`");
    }

    [Fact]
    public void A_delegated_contractor_with_no_readable_base_commit_skips_only_the_reset_and_recompose()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), "task/1-slug", _worktreePath, resumesPreviousWork: true,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Picking up midway.",
            delegationBaseCommit: null);

        prompt.Should().Contain("there is no");
        prompt.Should().Contain("boundary that is safe to reset to");
        prompt.Should().NotContain("git reset --mixed");
    }

    /// <summary>
    /// Task: a human at the wheel takes the fix role herself, fourth criterion — the starting
    /// prompt <c>h9k task work</c> hands the operator's own session teaches it the same four
    /// choices the review agents' outbound reports will, so a human agent can offer them in words
    /// without her reading the docs. Every command is asserted with its own id, because a choice
    /// named without its exact command is the one she still has to go and look up.
    /// </summary>
    [Fact]
    public void An_attended_interactive_mode_claim_is_taught_all_four_choices_at_the_fix_boundary()
    {
        TaskDetails task = SomeTask();
        task.InteractiveModeEnabled = true;

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true);

        prompt.Should().Contain("## The boundaries this task will park at, and the operator's choices there");
        prompt.Should().Contain($"`h9k review proceed {task.Id}`");
        prompt.Should().Contain($"`h9k review fixed {task.Id}`");
        prompt.Should().Contain($"`h9k review resolve {task.Id} --needs-fixes");
        prompt.Should().Contain($"`h9k review resolve {task.Id} --merge-ready");
        prompt.Should().Contain("--no-change", "the unmoved-tip override is part of the choice, not a footnote");
        prompt.Should().Contain(
            "can do the fix by hand",
            "the second choice is the one that is easy to miss, so the prompt says so out loud");
    }

    /// <summary>
    /// The same section names the hands-off exit at the gates-to-pull-request boundary — as an
    /// option, with interactive staying the default (Brian's ruling, 2026-09-07) — and names both
    /// steps it actually takes, since clearing the flag does not by itself release a park.
    /// </summary>
    [Fact]
    public void An_attended_interactive_mode_claim_is_taught_the_hands_off_pull_request_option()
    {
        TaskDetails task = SomeTask();
        task.InteractiveModeEnabled = true;

        string prompt = WorkPromptBuilder.Build(
            task, SomeProject(), "task/1-slug", _worktreePath, isInteractive: true);

        prompt.Should().Contain($"`h9k task revise {task.Id} --clear-interactive-mode`");
        prompt.Should().Contain($"then `h9k review proceed {task.Id}` once");
        prompt.Should().Contain("An option, never the default");
    }

    /// <summary>
    /// A task without the flag never sees any of it: the boundaries do not park for that task at
    /// all, so telling its session about her choices there would describe a lifecycle it is not in.
    /// </summary>
    [Fact]
    public void A_claim_without_interactive_mode_is_taught_nothing_about_boundary_choices()
    {
        string prompt = Build(isInteractive: true, isDeliberateHeadlessStart: false);

        prompt.Should().NotContain("The boundaries this task will park at");
        prompt.Should().NotContain("h9k review fixed");
    }

    private string Build(bool isInteractive, bool isDeliberateHeadlessStart) =>
        WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), branch: "task/abc12345-do-the-thing",
            worktreePath: _worktreePath,
            isInteractive: isInteractive, isDeliberateHeadlessStart: isDeliberateHeadlessStart);

    /// <summary>
    /// Decisions Log #163: the build session composes the pull request itself, so
    /// the marker and the skill order have to be in the prompt that asks for it, not only in the
    /// parser that reads it back.
    /// </summary>
    [Fact]
    public void The_headless_build_prompt_asks_for_the_pull_request_summary_block()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: false);

        prompt.Should().Contain(PrSummaryParser.Marker).And.Contain($"{PrSummaryParser.TitlePrefix} <one line>");
        prompt.IndexOf(PrSummaryParser.Marker, StringComparison.Ordinal)
            .Should().BeGreaterThan(prompt.IndexOf("verify tree identity", StringComparison.Ordinal),
                "it composes from the commits the recompose just made");
    }

    [Fact]
    public void The_headless_build_prompt_names_the_repos_own_rule_first_and_the_shipped_skill_second()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: false);

        prompt.IndexOf(".claude/commands/git/pr-description.md", StringComparison.Ordinal)
            .Should().BeGreaterThan(0).And
            .BeLessThan(prompt.IndexOf("Only when the repository ships none", StringComparison.Ordinal),
                "a repository with its own PR-description rule keeps its own voice");
        prompt.Should().Contain("`pr-summary` skill");
    }

    [Fact]
    public void The_pull_request_summary_step_says_what_the_platform_adds_around_it()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: false);

        prompt.Should().Contain("The work-item link, the acceptance criteria, and the run")
            .And.Contain("No em dashes (U+2014)")
            .And.Contain("Do not run `gh pr create` or `gh pr edit`");
    }

    /// <summary>
    /// The parser this step promises reads a headless session's own stream-json result payload,
    /// which an attended session never produces, so asking an operator's own session for the block
    /// would be asking for text nothing reads.
    /// </summary>
    [Fact]
    public void The_attended_interactive_prompt_never_asks_for_one()
    {
        Build(isInteractive: true, isDeliberateHeadlessStart: false).Should().NotContain(PrSummaryParser.Marker);
    }

    [Fact]
    public void The_delegated_contractor_prompt_asks_for_it_too()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), branch: "task/abc12345-do-the-thing", worktreePath: _worktreePath,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Picking up midway.",
            delegationBaseCommit: "abc1234");

        prompt.Should().Contain(PrSummaryParser.Marker).And.Contain(".claude/commands/git/pr-description.md");
    }

    /// <summary>
    /// The blast-radius arm: a contractor whose delegation base commit could not be read skips the
    /// recompose entirely, so it has no numbered step 4 to hang this on. Its final message is
    /// captured exactly the same way, so the step still applies, worded as a rule of its own.
    /// </summary>
    [Fact]
    public void A_contractor_that_cannot_recompose_is_still_asked_for_one()
    {
        string prompt = WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), branch: "task/abc12345-do-the-thing", worktreePath: _worktreePath,
            isDeliberateHeadlessStart: true, isDelegatedContractor: true, delegationNote: "Picking up midway.",
            delegationBaseCommit: null);

        prompt.Should().Contain(PrSummaryParser.Marker);
    }

    private static TaskDetails SomeTask() => new()
    {
        Id = DomainId.New(),
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
    };
}
