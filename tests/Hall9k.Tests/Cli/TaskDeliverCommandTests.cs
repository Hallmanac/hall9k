using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Storage;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskDeliverCommand.ReadHeadlessResult"/> against a scratch stream.jsonl, the shape
/// a start-it-mine claim's own detached <c>claude -p --output-format stream-json</c> session
/// writes and nothing else on this node ever adopts to read back (task 8a56af78-h9k, adversarial
/// review cycle 1: the node's periodic token-spend budget silently under-counted every session
/// <c>h9k task start</c> launched until delivery itself read this file).
/// </summary>
public sealed class TaskDeliverCommandTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-deliver-{Guid.NewGuid():N}");

    public TaskDeliverCommandTests() => Directory.CreateDirectory(_runDirectory);

    [Fact]
    public async Task Absent_stream_file_reads_as_no_handoff_and_no_usage()
    {
        TaskDeliverCommand.HeadlessResult result = TaskDeliverCommand.ReadHeadlessResult(_runDirectory);

        result.Handoff.Should().BeNull("an attended h9k task work claim never writes a stream.jsonl at all");
        result.Usage.Should().BeNull("there is no result line to have measured usage from");
    }

    [Fact]
    public async Task The_terminal_result_lines_usage_and_handoff_are_both_read_from_one_pass()
    {
        await WriteStreamFileAsync(
            """{"type":"assistant","message":{"content":[{"type":"text","text":"working..."}]}}""",
            """{"type":"result","is_error":false,"result":"HANDOFF: dependents need the new endpoint deployed first","usage":{"input_tokens":100,"cache_read_input_tokens":5000,"cache_creation_input_tokens":200,"output_tokens":50},"total_cost_usd":1.23}""");

        TaskDeliverCommand.HeadlessResult result = TaskDeliverCommand.ReadHeadlessResult(_runDirectory);

        result.Handoff.Should().Be("dependents need the new endpoint deployed first");
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(100);
        result.Usage.CacheReadInputTokens.Should().Be(5000);
        result.Usage.CacheCreationInputTokens.Should().Be(200);
        result.Usage.OutputTokens.Should().Be(50);
        result.Usage.CostUsd.Should().Be(1.23m);
    }

    [Fact]
    public async Task The_last_result_line_wins_when_more_than_one_is_present()
    {
        await WriteStreamFileAsync(
            """{"type":"result","is_error":false,"result":"first","usage":{"input_tokens":10,"output_tokens":5},"total_cost_usd":0.01}""",
            """{"type":"result","is_error":false,"result":"second","usage":{"input_tokens":20,"output_tokens":15},"total_cost_usd":0.02}""");

        TaskDeliverCommand.HeadlessResult result = TaskDeliverCommand.ReadHeadlessResult(_runDirectory);

        result.Usage!.InputTokens.Should().Be(20);
        result.Usage.OutputTokens.Should().Be(15);
        result.Usage.CostUsd.Should().Be(0.02m);
    }

    [Fact]
    public async Task A_result_line_with_no_usage_field_still_yields_zero_valued_usage_rather_than_null()
    {
        await WriteStreamFileAsync("""{"type":"result","is_error":false,"result":"done"}""");

        TaskDeliverCommand.HeadlessResult result = TaskDeliverCommand.ReadHeadlessResult(_runDirectory);

        // The result line itself was observed, so usage is an observed (zero) fact, not an
        // absent one — distinct from the no-file case above, where nothing was ever observed.
        result.Usage.Should().NotBeNull();
        result.Usage!.InputTokens.Should().Be(0);
        result.Usage.OutputTokens.Should().Be(0);
        result.Usage.CostUsd.Should().BeNull();
    }

    private async Task WriteStreamFileAsync(params string[] lines)
    {
        string streamFile = RunPaths.StreamFile(_runDirectory);
        await File.WriteAllLinesAsync(streamFile, lines);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
