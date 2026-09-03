using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The three delivery-arrangement sentences <see cref="WorkPromptBuilder.Build"/> can hand a
/// session, one per launch shape: a dispatcher-launched headless build (watched by
/// RunSupervisor), an operator's attached <c>h9k task work</c> claim, and a deliberate
/// <c>h9k task start</c> kick-off — headless like the first, but unsupervised like neither
/// (independent pre-PR review, cycle 1, both lenses: the session was being told the platform
/// verifies and opens the PR after it finishes, which is true of the first shape only).
/// </summary>
public sealed class WorkPromptBuilderTests
{
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

    private static string Build(bool isInteractive, bool isDeliberateHeadlessStart) =>
        WorkPromptBuilder.Build(
            SomeTask(), SomeProject(), branch: "task/abc12345-do-the-thing",
            worktreePath: Path.Combine(Path.GetTempPath(), $"hall9k-nonexistent-{Guid.NewGuid():N}"),
            isInteractive: isInteractive, isDeliberateHeadlessStart: isDeliberateHeadlessStart);

    private static TaskDetails SomeTask() => new()
    {
        Objective = "Add rate limiting to auth endpoints",
        AcceptanceCriteria = ["Requests over the limit get 429"],
    };

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
    };
}
