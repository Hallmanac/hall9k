using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="InteractiveSessionLiveness.IsSelfInvocation"/> is what lets h9k task
/// verify/deliver/handback/release skip the double-booking guard for the one caller safe to
/// exempt — the attached session asking about itself, blocked waiting on the command rather than
/// racing it. Two independent signals feed it: the legacy env var a direct launch injects into
/// its own child process, and CLAUDE_PID (Claude Code's own environment variable) matching the
/// run's recorded process id, which is the only signal available to a self-registered session
/// h9k never spawned. These mutate process-wide environment variables, so — per
/// <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>'s own blanket rule over every
/// <c>Environment.SetEnvironmentVariable</c>/<c>GetEnvironmentVariable</c> caller, not only
/// <c>HALL9K_HOME</c> itself — this class carries <c>[Collection("Hall9kHome")]</c> so it never
/// races a different collection's own env-var test. <see cref="EnvironmentVariableScope"/> itself
/// is a shared helper in <c>Hall9k.Tests.Fakes</c>, carrying the identical attribute on its own
/// declaration too — <c>HomeEnvironmentIsolationTests</c>'s own scan flags any class using these
/// members without it, helper included, even though the attribute has no runtime effect on a type
/// with no test methods of its own; this class still needs its own copy regardless, since the
/// helper's attribute does not extend serialization to a caller that omits it.
/// </summary>
[Collection("Hall9kHome")]
public sealed class InteractiveSessionLivenessTests
{
    [Fact]
    public void Reports_no_self_invocation_when_nothing_is_recorded_and_no_env_var_matches()
    {
        Guid runId = DomainId.New();
        RunDetails run = new() { Id = runId };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Clear(
            InteractiveSessionLiveness.InteractiveRunEnvironmentVariable,
            InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable);

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse();
    }

    [Fact]
    public void Recognises_a_direct_launch_child_by_the_legacy_env_var_alone()
    {
        Guid runId = DomainId.New();
        RunDetails run = new() { Id = runId };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, runId.ToString()),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, null));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeTrue(
            "a direct launch's own onStarted callback set this env var on the child it just spawned");
    }

    [Fact]
    public void Does_not_recognise_a_legacy_env_var_naming_a_different_run()
    {
        RunDetails run = new() { Id = DomainId.New() };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, DomainId.New().ToString()),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, null));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse();
    }

    [Fact]
    public void Recognises_a_self_registered_session_by_its_own_CLAUDE_PID_matching_the_recorded_process()
    {
        using Process current = Process.GetCurrentProcess();
        int pid = current.Id;
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(current);
        RunDetails run = new()
        {
            Id = DomainId.New(),
            SessionName = "abcd1234-" + SessionRoleName.InteractiveClaim,
            ActiveSessions = [new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, pid, startedAt)],
        };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, pid.ToString()));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeTrue(
            "this is the exact live process, at its exact recorded start time, that the run's own ActiveSessions entry names");
    }

    /// <summary>
    /// h9k task start records InteractiveSessionStarted under the identical AgentRole.Interactive
    /// a prompt-handoff or direct-launch claim does (TaskStartCommand mirrors that append
    /// exactly), so a bare role-plus-pid match cannot tell a deliberate headless claim apart from
    /// an attended one. Its own prompt never received AppendSelfDeliveryRule and states deliver
    /// and verify refuse unconditionally from inside it, so letting CLAUDE_PID recognise this
    /// session as its own would deliver mid-work with no handoff and no stop-editing rule ever
    /// stated to it (adversarial review, cycle 1).
    /// </summary>
    [Fact]
    public void Does_not_recognise_a_deliberate_headless_start_claim_even_when_CLAUDE_PID_matches()
    {
        using Process current = Process.GetCurrentProcess();
        int pid = current.Id;
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(current);
        RunDetails run = new()
        {
            Id = DomainId.New(),
            SessionName = "abcd1234-" + SessionRoleName.Build,
            ActiveSessions = [new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, pid, startedAt)],
        };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, pid.ToString()));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse(
            "a start-it-mine claim's own session never received the self-delivery rule, so it must still be refused as attached, not exempted");
    }

    [Fact]
    public void Does_not_recognise_a_CLAUDE_PID_that_does_not_match_the_recorded_process()
    {
        RunDetails run = new()
        {
            Id = DomainId.New(),
            ActiveSessions = [new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, 4242, DateTimeOffset.UtcNow)],
        };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, "9999"));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse(
            "a different pid is a different session, whatever CLAUDE_PID says about itself");
    }

    /// <summary>
    /// The pid-reuse guard: a registered session that exited without ever calling
    /// deliver/handback/release (nothing here appends an ended event the way a direct launch's own
    /// exit does) leaves its now-stale <see cref="ActiveSession"/> entry on record with a pid the
    /// OS can later hand to a genuinely unrelated process. A bare pid match alone would read that
    /// unrelated process as the same session; the recorded start time — deliberately wrong here —
    /// is what tells them apart.
    /// </summary>
    [Fact]
    public void Does_not_recognise_a_recycled_pid_whose_recorded_start_time_does_not_match()
    {
        using Process current = Process.GetCurrentProcess();
        int pid = current.Id;
        RunDetails run = new()
        {
            Id = DomainId.New(),
            ActiveSessions = [new ActiveSession(
                AgentRole.Interactive, ReviewLens.Unknown, pid, DateTimeOffset.UtcNow.AddDays(-1))],
        };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, pid.ToString()));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse(
            "this process's real start time does not match the recorded one, so the matching pid is a coincidence, not this session");
    }

    /// <summary>
    /// The cross-machine bypass this method's own doc comment used to claim was impossible
    /// (independent pre-PR review, both lenses, cycle 1, low): every one of today's four callers
    /// invokes this method *before* <see cref="InteractiveSessionLiveness.EnsureNotAttachedElsewhere"/>
    /// ever runs, so a bare pid-plus-start-time coincidence against a session recorded on a
    /// *different* machine used to read as self-invocation and skip that guard's own cross-machine
    /// --force refusal entirely. A pid this machine happens to share cannot make this machine's own
    /// process the one recorded on a different one.
    /// </summary>
    [Fact]
    public void Does_not_recognise_a_CLAUDE_PID_matching_the_recorded_pid_when_it_was_recorded_on_a_different_machine()
    {
        using Process current = Process.GetCurrentProcess();
        int pid = current.Id;
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(current);
        RunDetails run = new()
        {
            Id = DomainId.New(),
            ActiveSessions = [new ActiveSession(
                AgentRole.Interactive, ReviewLens.Unknown, pid, startedAt, "some-other-machine")],
        };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, pid.ToString()));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse(
            "the recorded session names a different machine, so this machine's own matching pid is a coincidence, not proof this is that session");
    }

    [Fact]
    public void Does_not_recognise_a_CLAUDE_PID_when_no_interactive_session_is_recorded_at_all()
    {
        RunDetails run = new() { Id = DomainId.New() };

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.InteractiveRunEnvironmentVariable, null),
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, "4242"));

        InteractiveSessionLiveness.IsSelfInvocation(run).Should().BeFalse(
            "nothing here claims a live session exists at all, so there is nothing to match CLAUDE_PID against");
    }

    [Fact]
    public void Reads_a_live_process_own_start_time()
    {
        using Process current = Process.GetCurrentProcess();

        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(current);

        startedAt.Should().NotBe(DateTimeOffset.MinValue, "the current process's own start time is always readable");
    }
}
