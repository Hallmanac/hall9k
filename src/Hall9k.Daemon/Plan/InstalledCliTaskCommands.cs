using System.Diagnostics;
using System.Text;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Plan;

/// <summary>
/// Runs the installed <c>h9k</c> as a child process for the task writes a plan needs. Deliberate, and not a shortcut:
/// the revision file's parser, the tracker item a publish files, and the ledger record all live in the CLI, and moving
/// them under the daemon would be a larger refactor than the plan itself. The child is hermetic about what a person's
/// shell would not be:
/// <list type="bullet">
/// <item>The binary is <see cref="DaemonRuntime.BinDirectory"/>'s, then whatever <c>PATH</c> finds, under the platform's own file name.</item>
/// <item>Its stdin is redirected and closed, so nothing it asks can wait on a terminal that is not there.</item>
/// <item>It carries the daemon's resolved connection string in <see cref="Hall9kDatabase.EnvironmentVariableName"/>, so it reaches the store this daemon reads even when the daemon was not started from the shell that configured it.</item>
/// <item>The <c>HALL9K_DISPATCHED_*</c> variables are cleared, because they mark a session the interceptor refuses these very commands for, and the daemon is not one.</item>
/// <item>It is killed, with its tree, if it outruns <see cref="DaemonOptions.PlanCommandTimeout"/>.</item>
/// </list>
/// Its output goes to a file in the plan's directory and nowhere else.
/// </summary>
public sealed class InstalledCliTaskCommands(
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<InstalledCliTaskCommands> logger) : IPlanTaskCommands
{
    private const string DispatchedPrefix = "HALL9K_DISPATCHED_";

    public Task<TaskCommandResult> ReviseAsync(
        Guid planId, Guid sessionId, Guid taskId, string revisionFile, CancellationToken cancellationToken) =>
        RunAsync(
            ["task", "revise", taskId.ToString(), "--file", revisionFile],
            RunPaths.PlanReviseOutputFile(planId, sessionId), cancellationToken,
            // Makes the child apply the revision only to the draft this session read, in one fenced append, and record it
            // as the session's own rather than as a person's revision made while the session ran.
            (TaskRevised.RefinementSessionEnvironmentVariable, sessionId.ToString()));

    public Task<TaskCommandResult> PublishAsync(
        Guid planId, Guid taskId, bool noExistingItem, bool untracked, CancellationToken cancellationToken)
    {
        List<string> arguments = ["task", "publish", taskId.ToString(), "--no-queue"];
        if (noExistingItem)
        {
            arguments.Add("--no-existing-item");
        }

        if (untracked)
        {
            arguments.Add("--untracked");
        }

        // Makes the child refuse a draft a person revised since a session last read it, in the fenced decision that publishes.
        return RunAsync(
            arguments, RunPaths.PlanPublishOutputFile(planId, taskId), cancellationToken,
            (TaskPublished.PlanPublishEnvironmentVariable, "1"));
    }

    /// <summary>The executable to run: the installed binary when it is there, else the bare name for <c>PATH</c> to resolve.</summary>
    internal static string ResolveExecutable()
    {
        string name = OperatingSystem.IsWindows()
            ? "h9k.exe"
            : "h9k";
        string installed = Path.Combine(DaemonRuntime.BinDirectory, name);
        return File.Exists(installed)
            ? installed
            : name;
    }

    private async Task<TaskCommandResult> RunAsync(
        IReadOnlyList<string> arguments, string outputFile, CancellationToken cancellationToken,
        (string Name, string Value)? environment = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new InvalidOperationException($"No directory for {outputFile}."));

        ProcessStartInfo start = new(ResolveExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith(DispatchedPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment[Hall9kDatabase.EnvironmentVariableName] = connection.ConnectionString;
        start.Environment.Remove(TaskRevised.RefinementSessionEnvironmentVariable);
        start.Environment.Remove(TaskPublished.PlanPublishEnvironmentVariable);
        if (environment is { } extra)
        {
            start.Environment[extra.Name] = extra.Value;
        }

        await using FileStream output = new(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using StreamWriter writer = new(output, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync($"$ h9k {string.Join(' ', arguments)}");

        using Process process = new() { StartInfo = start };
        SemaphoreSlim writeLock = new(1, 1);
        async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                await writeLock.WaitAsync(CancellationToken.None);
                try
                {
                    await writer.WriteLineAsync(line);
                }
                finally
                {
                    writeLock.Release();
                }
            }
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(exception, "Could not start {Executable} for a plan", start.FileName);
            await writer.WriteLineAsync($"could not start {start.FileName}: {exception.Message}");
            return new TaskCommandResult(-1, false, outputFile);
        }

        process.StandardInput.Close();
        Task pumps = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));

        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(options.Value.PlanCommandTimeout);
        try
        {
            await process.WaitForExitAsync(bound.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await pumps;
            await writer.WriteLineAsync($"killed: still running after {options.Value.PlanCommandTimeout}");
            return new TaskCommandResult(-1, true, outputFile);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        await pumps;
        return new TaskCommandResult(process.ExitCode, false, outputFile);
    }

    private void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not kill the child h9k process for a plan");
        }
    }
}
