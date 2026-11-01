using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
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
/// The lesson picker's no-tools argument as it actually reaches a process: spawned through
/// <see cref="ClaudeExecutor"/> and the real process manager for this operating system, with a
/// stand-in for the claude binary (<c>HALL9K_CLAUDE_PATH</c>) that writes down what it was handed.
/// <c>--tools</c> is variadic and its empty value is what disables every tool, so a platform shell
/// that dropped the empty quotes would leave the session with all its tools while every argument
/// test on the composed string still passed. On Windows the stand-in is a batch file that echoes
/// its raw command line, because a batch file cannot tell an empty argument from a missing one; the
/// quotes arriving, last on the line, is the observation.
/// </summary>
[Collection("Environment")]
[Trait("Category", "Environment")]
public sealed class ClaudeExecutorNoToolsSpawnTests : IDisposable
{
    // The stand-in writes this last, so seeing it means every argument is already in the file.
    private const string DoneMarker = "hall9k-stand-in-done";

    private static readonly TimeSpan ObservationDeadline = TimeSpan.FromSeconds(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("hall9k-no-tools-spawn-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_empty_value_of_the_no_tools_argument_reaches_the_process_as_the_last_argument()
    {
        string standIn = OperatingSystem.IsWindows() ? WriteWindowsStandIn() : WriteUnixStandIn();
        string runDirectory = Path.Combine(_directory, "run");
        Directory.CreateDirectory(runDirectory);
        using EnvironmentVariableScope claude = EnvironmentVariableScope.Set(("HALL9K_CLAUDE_PATH", standIn));
        ClaudeExecutor executor = new(
            NullLogger<ClaudeExecutor>.Instance,
            ProcessManagers.ForCurrentPlatform(),
            Options.Create(new DaemonOptions { SessionContainment = false }),
            SessionContainment.Off);
        Guid runId = DomainId.New();

        SpawnedAgent agent = await executor.SpawnAsync(
            new AgentSpawnRequest(
                runId, DomainId.New(), runDirectory, runDirectory, "pick some lessons", ExecutorMode.Subscription,
                AgentModel.Sonnet, AgentEffort.Unknown, SkipPermissions: false, SessionArtifactName: "lesson-picker-test",
                UntrustedWorkingDirectory: true, MaxTurns: 1, NoTools: true)
            {
                SessionName = "test-lesson-picker",
            },
            CancellationToken.None);

        agent.ProcessId.Should().BePositive();
        string received = await ReadWhenWrittenAsync(RunPaths.SessionStreamFile(runDirectory, "lesson-picker-test"));

        if (OperatingSystem.IsWindows())
        {
            received.Trim().Should().EndWith("--tools \"\"",
                "the platform shell kept the empty quotes, and nothing follows them on the command line");
        }
        else
        {
            string[] arguments = [.. received.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')];
            arguments[^2..].Should().Equal(["[--tools]", "[]"],
                "the empty value arrived as an argument of its own, last on the line");
            arguments.Should().Contain("[--max-turns]").And.Contain("[1]");
        }
    }

    private string WriteUnixStandIn()
    {
        string path = Path.Combine(_directory, "fake-claude.sh");
        // One bracketed line per argument, so an empty argument shows as "[]" rather than as nothing.
        File.WriteAllText(path, $"#!/bin/sh\nfor argument in \"$@\"; do printf '[%s]\\n' \"$argument\"; done\necho {DoneMarker}\n");
        using Process chmod = Process.Start(new ProcessStartInfo("chmod") { ArgumentList = { "+x", path } })!;
        chmod.WaitForExit();
        return path;
    }

    private string WriteWindowsStandIn()
    {
        string path = Path.Combine(_directory, "fake-claude.cmd");
        File.WriteAllText(path, $"@echo off\r\necho %*\r\necho {DoneMarker}\r\n");
        return path;
    }

    private static async Task<string> ReadWhenWrittenAsync(string file)
    {
        DateTime deadline = DateTime.UtcNow + ObservationDeadline;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(file))
            {
                string? text = await TryReadAsync(file);
                int marker = text?.IndexOf(DoneMarker, StringComparison.Ordinal) ?? -1;
                if (text is not null && marker >= 0 && text.EndsWith('\n'))
                {
                    return text[..marker];
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"The stand-in for claude wrote nothing to {file} within {ObservationDeadline}.");
    }

    // On Windows the stand-in still holds the file open for writing, so the read can hit a sharing
    // violation; that means "not written yet", and the caller keeps polling until its deadline.
    private static async Task<string?> TryReadAsync(string file)
    {
        try
        {
            return await File.ReadAllTextAsync(file);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
