using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The model each session runs on is a platform decision, resolved and recorded rather
/// than inherited (Decisions Log #33). Two halves are worth asserting without a database:
/// the node's role-aware resolution chain, and the spawn flags that carry its answer to
/// claude. The origin incident (2026-08-20) was invisible precisely because no flag was
/// ever passed.
/// </summary>
public sealed class ModelPolicyTests
{
    [Fact]
    public void The_shipped_configuration_puts_every_role_on_one_explicit_model()
    {
        DaemonOptions options = new();

        foreach (AgentRole role in new[] { AgentRole.Build, AgentRole.Review, AgentRole.Fix, AgentRole.Refinement })
        {
            options.ResolveModel(role, taskModel: null, projectModel: null)
                .Value.Should().Be(AgentModel.PlatformFallback,
                    $"the {role.Value} role ships with no opinion of its own; the knob and the record are the point");
        }
    }

    /// <summary>
    /// The shipped default is spelled out here rather than compared to the constant, because
    /// the constant is exactly what a future edit could narrow without noticing. Sessions were
    /// observed running on the 1M-context variant when this default was chosen (Decisions Log
    /// #33); dropping to standard context would be the same silent, unrecorded change of model
    /// that this whole feature exists to make impossible.
    /// </summary>
    [Fact]
    public void The_shipped_default_is_the_one_million_context_variant_and_is_spawnable()
    {
        AgentModel shipped = new DaemonOptions().ResolveModel(AgentRole.Build, taskModel: null, projectModel: null);

        shipped.Value.Should().Be("claude-opus-5[1m]",
            "narrowing the platform default to standard context is a model change, and a model change is never silent");
        shipped.IsWellFormed.Should().BeTrue("the platform default has to survive the executor's own refusal to spawn");
    }

    [Fact]
    public void Each_role_is_independently_configurable()
    {
        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", Fix = "haiku" },
        };

        options.ResolveModel(AgentRole.Review, null, null).Should().Be(AgentModel.Sonnet);
        options.ResolveModel(AgentRole.Fix, null, null).Should().Be(AgentModel.Haiku);
        options.ResolveModel(AgentRole.Build, null, null).Value.Should().Be(
            "claude-opus-5", "a role left unset falls through to the platform default");
    }

    [Fact]
    public void The_chain_runs_task_then_role_then_project_then_platform()
    {
        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet" },
        };

        options.ResolveModel(AgentRole.Review, AgentModel.Fable, AgentModel.Haiku)
            .Should().Be(AgentModel.Fable, "the task override is the most specific level");
        options.ResolveModel(AgentRole.Review, AgentModel.Unknown, AgentModel.Haiku)
            .Should().Be(AgentModel.Sonnet, "the role default outranks the project default");
        options.ResolveModel(AgentRole.Build, AgentModel.Unknown, AgentModel.Haiku)
            .Should().Be(AgentModel.Haiku, "with no role opinion, the project default decides");
    }

    /// <summary>
    /// A Verify pass's model is a narrower knob under Review, not a seventh role (Brian's ruling,
    /// 2026-08-29): left unset, it must fall through to exactly what Review itself resolves to on
    /// the same task/project, including when Review carries its own configured override — the
    /// acceptance bar for "the knob defaults to the review model, no behavior change until set".
    /// </summary>
    [Fact]
    public void Unset_review_verify_falls_through_to_whatever_review_itself_resolves_to()
    {
        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet" },
        };

        options.ResolveVerifyReviewModel(taskModel: null, projectModel: null).Should().Be(AgentModel.Sonnet);
        options.ResolveVerifyReviewModel(taskModel: null, projectModel: AgentModel.Haiku).Should().Be(
            AgentModel.Sonnet, "Review's own configured override still outranks the project default underneath it");
    }

    [Fact]
    public void A_configured_review_verify_model_outranks_the_plain_review_chain()
    {
        DaemonOptions options = new()
        {
            DefaultModel = "claude-opus-5",
            ModelByRole = new RoleModelDefaults { Review = "sonnet", ReviewVerify = "haiku" },
        };

        options.ResolveVerifyReviewModel(taskModel: null, projectModel: null).Should().Be(AgentModel.Haiku);
    }

    [Fact]
    public void A_task_override_still_wins_over_the_review_verify_knob()
    {
        DaemonOptions options = new()
        {
            ModelByRole = new RoleModelDefaults { Review = "sonnet", ReviewVerify = "haiku" },
        };

        options.ResolveVerifyReviewModel(taskModel: AgentModel.Fable, projectModel: null)
            .Should().Be(AgentModel.Fable, "a task-level override is the most specific level for every pass, Verify included");
    }

    [Fact]
    public void A_fresh_spawn_always_states_its_model()
    {
        Guid runId = DomainId.New();
        AgentSpawnRequest request = new(
            runId, DomainId.New(), "/tmp/worktree", "/tmp/run", "prompt", ExecutorMode.Subscription,
            AgentModel.FromInput("claude-opus-5[1m]"), SkipPermissions: true);

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().Contain("--model \"claude-opus-5[1m]\"",
            "the model is quoted so an id carrying shell glob characters reaches claude intact");
        arguments.Should().Contain($"--session-id {request.SessionId}");
    }

    [Fact]
    public void A_resumed_session_is_never_re_pointed_at_a_model()
    {
        Guid resumed = DomainId.New();
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/worktree", "/tmp/run", "conclude", ExecutorMode.Subscription,
            AgentModel.Sonnet, SkipPermissions: false, SessionArtifactName: "review-1-abc",
            ResumeSessionId: resumed);

        string[] arguments = [.. ClaudeExecutor.Arguments(request)];

        arguments.Should().Contain($"--resume {resumed}");
        arguments.Should().NotContain(argument => argument.StartsWith("--model", StringComparison.Ordinal),
            "a resumed session keeps the model it started with; the carried value is for the record, not the process");
    }

    [Fact]
    public async Task A_spawn_that_reached_the_executor_without_a_model_is_refused_rather_than_inherited()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        ClaudeExecutor executor = new(
            NullLogger<ClaudeExecutor>.Instance, new FakeProcessManager(), Options.Create(new DaemonOptions()));
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), "/tmp/worktree", "/tmp/run", "prompt", ExecutorMode.Subscription,
            AgentModel.Unknown, SkipPermissions: false);

        Func<Task> spawn = () => executor.SpawnAsync(request, cts.Token);

        await spawn.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*never inherits the owner's personal default*");
    }
}
