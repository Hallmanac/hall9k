using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The node's configured effort level reaches a dispatched session only through the settings file
/// <see cref="ClaudeExecutor"/> writes and hands to <c>--settings</c>, because a headless session
/// ignores the owner's user-level <c>effortLevel</c>. These tests spawn through the executor itself
/// so the option, the file and the argument are proven to line up, not just the file builder.
/// </summary>
public sealed class ClaudeExecutorEffortTests : IDisposable
{
    private readonly string runDirectory =
        Path.Combine(Path.GetTempPath(), $"h9k-effort-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(runDirectory))
        {
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task A_configured_effort_lands_in_the_settings_file_the_session_is_handed(string level)
    {
        using JsonDocument settings = await SpawnAndReadSettingsAsync(new DaemonOptions { Effort = level });

        settings.RootElement.GetProperty("effortLevel").GetString().Should().Be(level);
    }

    [Fact]
    public async Task A_differently_cased_effort_is_written_in_its_canonical_form()
    {
        using JsonDocument settings = await SpawnAndReadSettingsAsync(new DaemonOptions { Effort = " HIGH " });

        settings.RootElement.GetProperty("effortLevel").GetString().Should().Be("high");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("ludicrous")]
    [InlineData("high\", \"includeCoAuthoredBy\": true, \"x\": \"")]
    public async Task An_unset_or_unrecognized_effort_leaves_the_key_out_entirely(string? level)
    {
        using JsonDocument settings = await SpawnAndReadSettingsAsync(new DaemonOptions { Effort = level });

        settings.RootElement.TryGetProperty("effortLevel", out _).Should().BeFalse();
        settings.RootElement.GetProperty("includeCoAuthoredBy").GetBoolean().Should().BeFalse(
            "a value that is not one of the five names must never reach, let alone rewrite, the generated file");
    }

    private async Task<JsonDocument> SpawnAndReadSettingsAsync(DaemonOptions options)
    {
        FakeProcessManager processes = new();
        ClaudeExecutor executor = new(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(options));
        AgentSpawnRequest request = new(
            DomainId.New(), DomainId.New(), Path.GetTempPath(), runDirectory, "prompt",
            ExecutorMode.Subscription, AgentModel.Sonnet, SkipPermissions: false)
        {
            SessionName = "test-build",
        };

        await executor.SpawnAsync(request, CancellationToken.None);

        return JsonDocument.Parse(await File.ReadAllTextAsync(RunPaths.SettingsFile(runDirectory)));
    }
}
