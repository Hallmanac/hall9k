using System.Diagnostics;
using FluentAssertions;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Runs <c>git</c> directly, one argument per <see cref="ProcessStartInfo.ArgumentList"/> entry,
/// for the tests that seed a throwaway repository. The helper exists because those seeds used to
/// go through <c>/bin/sh -c "git init -q -b main &amp;&amp; git commit ..."</c>, where the shell
/// was only ever gluing git calls together with <c>&amp;&amp;</c> — a Unix-only spawn that threw
/// <c>Win32Exception</c> before the first git ran on a Windows node (origin: the Windows
/// full-suite baseline of 2026-09-08, 48 failures across
/// <c>Hall9k.Tests.Integration.VerificationRunnerTests</c> and
/// <c>Hall9k.Tests.Integration.RunSupervisorTests</c>). Calling git itself needs no shell at all,
/// so there is nothing left to be platform-specific about, and the argument list carries commit
/// messages and paths without any quoting for a shell to re-parse.
/// </summary>
internal static class TestGit
{
    /// <summary>
    /// Runs one git command and asserts it succeeded, quoting git's own output in the failure
    /// message — a seed that half-ran is otherwise diagnosed from whatever the test asserted
    /// afterwards rather than from what git actually said.
    /// </summary>
    public static async Task RunAsync(
        string workingDirectory, string[] arguments, CancellationToken cancellationToken) =>
        await CaptureAsync(workingDirectory, arguments, cancellationToken);

    /// <summary>
    /// Runs one git command, asserts it succeeded, and returns its standard output.
    /// </summary>
    public static async Task<string> CaptureAsync(
        string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await standardOutput;
        string error = await standardError;

        process.ExitCode.Should().Be(
            0,
            $"'git {string.Join(' ', arguments)}' in '{workingDirectory}' must succeed for the test repo " +
            $"to be usable, but it said: {output}{error}");
        return output;
    }

    /// <summary>
    /// The identity flags every seeding commit carries, so no test depends on the machine's own
    /// <c>user.name</c>/<c>user.email</c> being configured.
    /// </summary>
    public static string[] CommitAs(params string[] arguments) =>
        ["-c", "user.email=t@t", "-c", "user.name=t", .. arguments];
}
