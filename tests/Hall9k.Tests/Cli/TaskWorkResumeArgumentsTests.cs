using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Decisions Log #122 (amending #103): re-entering an interactive claim attempts
/// <c>claude --resume &lt;recorded session id&gt;</c> before ever minting a fresh one. These
/// assert the flag set directly, mirroring <c>ClaudeExecutorIsolationTests</c>'s own
/// internal-for-policy-tests reasoning — the flag set IS the policy, worth checking without
/// spawning a real <c>claude</c> process.
/// </summary>
public sealed class TaskWorkResumeArgumentsTests
{
    [Fact]
    public void A_resume_attempt_carries_resume_and_never_session_id_or_model()
    {
        Guid sessionId = Guid.NewGuid();

        string[] arguments = [.. TaskWorkCommand.BuildInteractiveArguments(
            sessionId, true, "864c7f30-interactive-claim", "/tmp/settings.json", false, "prompt text")];

        arguments.Should().ContainInOrder("--resume", sessionId.ToString());
        arguments.Should().NotContain("--session-id",
            "a resume re-enters the recorded conversation; --session-id is for a fresh session only and would conflict with it");
        arguments.Should().NotContain("--model",
            "a resumed session keeps the model it started with, mirroring ClaudeExecutor's own headless resume branch (#5)");
        arguments.Should().Contain("--name");
        arguments.Should().ContainInOrder("--name", "864c7f30-interactive-claim");
        arguments.Should().ContainInOrder("--settings", "/tmp/settings.json");
        arguments[^1].Should().Be("prompt text", "the prompt is always the trailing positional argument");
    }

    [Fact]
    public void A_fresh_launch_carries_session_id_and_model_and_never_resume()
    {
        Guid sessionId = Guid.NewGuid();

        string[] arguments = [.. TaskWorkCommand.BuildInteractiveArguments(
            sessionId, false, "864c7f30-interactive-claim", "/tmp/settings.json", false, "prompt text")];

        arguments.Should().ContainInOrder("--session-id", sessionId.ToString());
        arguments.Should().ContainInOrder("--model", "fable");
        arguments.Should().NotContain("--resume");
    }

    [Fact]
    public void Skip_permissions_adds_the_dangerous_flag_on_either_path()
    {
        Guid sessionId = Guid.NewGuid();

        string[] resumeArguments = [.. TaskWorkCommand.BuildInteractiveArguments(
            sessionId, true, "name", "/tmp/settings.json", true, "prompt")];
        string[] freshArguments = [.. TaskWorkCommand.BuildInteractiveArguments(
            sessionId, false, "name", "/tmp/settings.json", true, "prompt")];

        resumeArguments.Should().Contain("--dangerously-skip-permissions");
        freshArguments.Should().Contain("--dangerously-skip-permissions");
    }

    [Fact]
    public void The_recorded_no_conversation_message_is_recognised()
    {
        Guid sessionId = Guid.NewGuid();
        string stderr = $"No conversation found with session ID: {sessionId}\n";

        TaskWorkCommand.IsResumeNotFoundError(stderr).Should().BeTrue();
    }

    [Fact]
    public void Unrelated_stderr_output_is_not_mistaken_for_a_resume_failure()
    {
        TaskWorkCommand.IsResumeNotFoundError("some other error entirely").Should().BeFalse();
        TaskWorkCommand.IsResumeNotFoundError(string.Empty).Should().BeFalse();
    }
}
