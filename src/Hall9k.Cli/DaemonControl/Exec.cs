using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Cli.DaemonControl;

public sealed record ExecResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Run a tool (launchctl, kill, dotnet publish), capture everything, never throw on a nonzero exit.</summary>
public static class Exec
{
    public static async Task<ExecResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The class's own contract is "never throw on a nonzero exit" — a launch that
            // never got a process at all is the same kind of failure to a caller checking
            // Succeeded, not a reason to let a raw framework exception escape instead
            // (cycle-1 pre-PR review finding: IsLoadedAsync had no degradation path here,
            // unlike every other probe in WindowsDaemonAutostart).
            return new ExecResult(-1, string.Empty, exception.Message);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ExecResult(process.ExitCode, await standardOutput, await standardError);
    }
}
