using FluentAssertions;
using Hall9k.Connectors.Prompts;
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
/// kick-off: headless like the first, but unsupervised like neither (independent pre-PR review,
/// cycle 1, both lenses: the session was being told the platform verifies and opens the PR after
/// it finishes, which is true of the first shape only).
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
    /// h9k task start's own claim carries the ceiling-exempt sentinel Guid.Empty NodeId, so
    /// RunSupervisor never adopts it (AdoptOrphansAsync/ResumeStrandedPipelinesAsync both filter
    /// on r.NodeId == nodeId) — nothing verifies or opens a pull request until a human runs
    /// h9k task deliver by hand, so the prompt must say that rather than either of the other two
    /// claims.
    /// </summary>
    [Fact]
    public void A_deliberate_headless_start_is_told_nothing_supervises_it_and_delivery_is_manual()
    {
        string prompt = Build(isInteractive: false, isDeliberateHeadlessStart: true);

        prompt.Should().Contain("nothing supervises this run");
        prompt.Should().Contain("`h9k task deliver` pushes the branch");
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

        prompt.Should().Contain("a human's to trigger by hand");
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

    private string Build(bool isInteractive, bool isDeliberateHeadlessStart) =>
        WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), branch: "task/abc12345-do-the-thing",
            worktreePath: _worktreePath,
            isInteractive: isInteractive, isDeliberateHeadlessStart: isDeliberateHeadlessStart);

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
