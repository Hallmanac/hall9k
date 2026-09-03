using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskRegisterSessionCommand.ReadClaudeProcess"/> and
/// <see cref="TaskRegisterSessionCommand.ReadClaudeSessionId"/> are the DB-free half of the
/// self-registration observation gate: what this command can actually observe from the calling
/// session's own environment, and the honest refusal when it cannot. The store round trip itself
/// (the append, the Claimed+interactive and run-state guards) is this command's own
/// integration-tier concern, the same split <see cref="TaskLogInteractionCommandTests"/> already
/// draws for its own command. These mutate CLAUDE_PID and CLAUDE_CODE_SESSION_ID, environment
/// variables, so — per <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>'s own
/// blanket rule over every <c>Environment.SetEnvironmentVariable</c>/<c>GetEnvironmentVariable</c>
/// caller, not only <c>HALL9K_HOME</c> itself — both this class and its nested scope helper carry
/// <c>[Collection("Hall9kHome")]</c> so they never race a different collection's own env-var test.
/// </summary>
[Collection("Hall9kHome")]
public sealed class TaskRegisterSessionCommandTests
{
    [Fact]
    public void Refuses_when_CLAUDE_PID_is_not_set()
    {
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_PID", null));

        Action act = () => TaskRegisterSessionCommand.ReadClaudeProcess(DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*CLAUDE_PID*");
    }

    [Fact]
    public void Refuses_when_CLAUDE_PID_is_not_a_number()
    {
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_PID", "not-a-pid"));

        Action act = () => TaskRegisterSessionCommand.ReadClaudeProcess(DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*CLAUDE_PID*");
    }

    [Fact]
    public void Refuses_when_CLAUDE_PID_names_a_process_this_machine_cannot_find()
    {
        // No real process plausibly holds this pid on any platform this suite runs on.
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_PID", int.MaxValue.ToString()));

        Action act = () => TaskRegisterSessionCommand.ReadClaudeProcess(DomainId.New());

        act.Should().Throw<DomainConflictException>().WithMessage("*could not be found*");
    }

    [Fact]
    public void Reads_a_live_process_pid_and_start_time_when_CLAUDE_PID_names_this_test_process()
    {
        int thisProcessId = Environment.ProcessId;
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_PID", thisProcessId.ToString()));

        (int processId, DateTimeOffset startedAt) = TaskRegisterSessionCommand.ReadClaudeProcess(DomainId.New());

        processId.Should().Be(thisProcessId);
        startedAt.Should().NotBe(DateTimeOffset.MinValue, "this test's own process is definitely alive with a readable start time");
    }

    [Fact]
    public void Reads_the_sessions_own_CLAUDE_CODE_SESSION_ID_when_it_parses()
    {
        Guid expected = DomainId.New();
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_CODE_SESSION_ID", expected.ToString()));

        TaskRegisterSessionCommand.ReadClaudeSessionId().Should().Be(expected);
    }

    [Fact]
    public void Mints_a_fresh_id_when_CLAUDE_CODE_SESSION_ID_is_absent()
    {
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_CODE_SESSION_ID", null));

        TaskRegisterSessionCommand.ReadClaudeSessionId().Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Mints_a_fresh_id_when_CLAUDE_CODE_SESSION_ID_does_not_parse_as_a_guid()
    {
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(("CLAUDE_CODE_SESSION_ID", "not-a-guid"));

        TaskRegisterSessionCommand.ReadClaudeSessionId().Should().NotBe(Guid.Empty);
    }

    /// <summary>Saves and restores the named environment variables around a test, isolating it from every other.</summary>
    [Collection("Hall9kHome")]
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _saved;

        private EnvironmentVariableScope((string Name, string? Previous)[] saved) => _saved = saved;

        public static EnvironmentVariableScope Set(params (string Name, string? Value)[] values)
        {
            (string Name, string? Previous)[] saved =
                [.. values.Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))];
            foreach ((string name, string? value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            return new EnvironmentVariableScope(saved);
        }

        public void Dispose()
        {
            foreach ((string name, string? previous) in _saved)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
