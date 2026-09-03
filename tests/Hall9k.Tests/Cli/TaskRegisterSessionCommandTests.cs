using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
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
/// caller, not only <c>HALL9K_HOME</c> itself — this class carries <c>[Collection("Hall9kHome")]</c>
/// so it never races a different collection's own env-var test; the scope helper itself,
/// <see cref="EnvironmentVariableScope"/>, is shared from <c>Hall9k.Tests.Fakes</c> rather than
/// nested here.
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

    [Fact]
    public void Reads_the_sessions_own_name_from_its_claude_sessions_file()
    {
        string sessionsDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-claude-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionsDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(sessionsDirectory, "4242.json"),
                """{"pid":4242,"name":"74a18e83-interactive-claim","nameSource":"user"}""");

            TaskRegisterSessionCommand.ReadClaudeSessionName(4242, sessionsDirectory)
                .Should().Be("74a18e83-interactive-claim");
        }
        finally
        {
            Directory.Delete(sessionsDirectory, recursive: true);
        }
    }

    [Fact]
    public void Reads_null_when_no_session_file_exists_for_the_pid()
    {
        string sessionsDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-claude-sessions-{Guid.NewGuid():N}");

        TaskRegisterSessionCommand.ReadClaudeSessionName(4242, sessionsDirectory).Should().BeNull();
    }

    [Fact]
    public void Reads_null_when_the_session_file_carries_no_name_field()
    {
        string sessionsDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-claude-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(sessionsDirectory, "4242.json"), """{"pid":4242}""");

            TaskRegisterSessionCommand.ReadClaudeSessionName(4242, sessionsDirectory).Should().BeNull();
        }
        finally
        {
            Directory.Delete(sessionsDirectory, recursive: true);
        }
    }

    [Fact]
    public void Reads_null_when_the_session_file_is_not_valid_json()
    {
        string sessionsDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-claude-sessions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(sessionsDirectory, "4242.json"), "not json");

            TaskRegisterSessionCommand.ReadClaudeSessionName(4242, sessionsDirectory).Should().BeNull();
        }
        finally
        {
            Directory.Delete(sessionsDirectory, recursive: true);
        }
    }
}
