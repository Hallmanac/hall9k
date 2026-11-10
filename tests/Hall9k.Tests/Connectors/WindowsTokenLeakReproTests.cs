using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Xunit;
using Xunit.Abstractions;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// One xUnit collection with parallelization disabled: the test below reads a machine-wide
/// kernel counter, so any other test creating processes while it runs would land in the reading.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TokenLeakReproCollection
{
    public const string Name = "TokenLeakRepro";
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless this is Windows and
/// <see cref="WindowsTokenLeakReproTests.OptInVariable"/> is set to <c>1</c>, so the branch's own
/// build and test gates pass untouched.
/// </summary>
public sealed class WindowsTokenLeakReproFactAttribute : FactAttribute
{
    public WindowsTokenLeakReproFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: it counts live access-token objects in the Windows kernel pool.";
        }
        else if (Environment.GetEnvironmentVariable(WindowsTokenLeakReproTests.OptInVariable) != "1")
        {
            Skip = $"Opt-in: set {WindowsTokenLeakReproTests.OptInVariable}=1 and run elevated "
                + "(about two minutes; it starts 1,200 child processes one at a time).";
        }
    }
}

/// <summary>
/// Spike reproduction (task 54c44399's cause hunt): on this node's Windows build, a launched
/// program that itself starts a child and is still running when that child exits leaves its own
/// kernel access token (pool tag <c>Toke</c>) allocated for good. Git for Windows' redirector,
/// <c>cmd\git.exe</c>, is exactly that program: it starts the real git and waits for it, and
/// <c>git</c> on PATH resolves to it, which is how h9kd reaches it on every call.
/// <para>
/// Both arms start the same command, <c>git --version</c>, through the shared runner
/// (<see cref="ExternalProcess.Runner"/>), one child at a time. They differ only in which
/// executable that is: the redirector (the condition) or the real <c>git.exe</c> beside it, which
/// starts nothing (the control, the condition removed by hand). The arms alternate in
/// equal-length windows, and which arm goes first flips every round, so the daemon's own
/// background token growth and the first-in-pair bias land on both alike.
/// </para>
/// <para>
/// It FAILS when the condition arm leaves at least 100 more live <c>Toke</c> tokens than the
/// control arm, which is what the spike's recorded run shows. A fix that stops the leak makes it
/// pass. Reading the count needs an elevated process.
/// </para>
/// </summary>
[Collection(TokenLeakReproCollection.Name)]
public sealed class WindowsTokenLeakReproTests(ITestOutputHelper output)
{
    /// <summary>The opt-in switch: the test is skipped unless this is <c>1</c>.</summary>
    public const string OptInVariable = "HALL9K_TOKEN_LEAK_REPRO";

    private const int ChildrenPerBatch = 200;
    private const int Rounds = 6;
    private const int WarmUpChildren = 20;
    private const int FailureThreshold = 100;

    // Long enough for 200 launches (about 7.5 s for the redirector) and for the tokens of the
    // children that just exited to be freed (observed up to about 5 s), and the same for both
    // arms so the background growth is equal on both sides.
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(15);

    [WindowsTokenLeakReproFact]
    public async Task Launching_a_program_that_starts_and_outlives_its_own_child_leaks_a_token_the_real_git_does_not()
    {
        (string redirector, string realGit) = LocateGit();
        string workingDirectory = Path.GetTempPath();
        StringBuilder log = new();
        long conditionTotal = 0;
        long controlTotal = 0;

        // Not counted: the first batch of a run reads high (first-time image scans, JIT, the
        // daemon reacting to new children), which would land on whichever arm goes first.
        await MeasureBatchAsync("warm-up (not counted)", redirector, workingDirectory, log, WarmUpChildren);
        await MeasureBatchAsync("warm-up (not counted)", realGit, workingDirectory, log, WarmUpChildren);

        for (int round = 1; round <= Rounds; round++)
        {
            // The arm that goes first in a pair reads about half a token per launch high even when
            // both arms start the same program (a null run of real git against real git showed it),
            // so the order alternates and the bias cancels in the totals.
            bool conditionFirst = round % 2 == 1;
            long condition;
            long control;
            if (conditionFirst)
            {
                condition = await MeasureBatchAsync($"round {round} condition (redirector)", redirector, workingDirectory, log);
                control = await MeasureBatchAsync($"round {round} control (real git)", realGit, workingDirectory, log);
            }
            else
            {
                control = await MeasureBatchAsync($"round {round} control (real git)", realGit, workingDirectory, log);
                condition = await MeasureBatchAsync($"round {round} condition (redirector)", redirector, workingDirectory, log);
            }

            conditionTotal += condition;
            controlTotal += control;
            Report(log, $"round {round}: condition +{condition}, control +{control}, difference {condition - control} "
                + $"({(condition - control) / (double)ChildrenPerBatch:F2} per launch)");
        }

        long difference = conditionTotal - controlTotal;
        int launches = ChildrenPerBatch * Rounds;
        Report(log, $"TOTAL over {launches} launches per arm: condition +{conditionTotal}, control +{controlTotal}, "
            + $"difference {difference} ({difference / (double)launches:F2} per launch)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "h9k-token-leak-repro.log"), log.ToString());

        difference.Should().BeLessThan(
            FailureThreshold,
            $"the redirector arm ({redirector}) left {difference} more live Toke tokens than the real git arm "
            + $"({realGit}) across {launches} launches each: a launched program that starts a child and is still "
            + "running when it exits keeps its own kernel token alive for good");
    }

    private async Task<long> MeasureBatchAsync(
        string label, string git, string workingDirectory, StringBuilder log, int children = ChildrenPerBatch)
    {
        Stopwatch window = Stopwatch.StartNew();
        long before = PoolTags.LiveTokens();
        for (int index = 0; index < children; index++)
        {
            ProcessResult result = await ExternalProcess.Runner(git, ["--version"], workingDirectory, CancellationToken.None);
            result.ExitCode.Should().Be(0, $"{git} --version must succeed for the batch to mean anything");
        }

        TimeSpan launching = window.Elapsed;
        if (window.Elapsed < BatchWindow)
        {
            await Task.Delay(BatchWindow - window.Elapsed);
        }

        long after = PoolTags.LiveTokens();
        Report(log, $"{label}: {children} launches in {launching.TotalSeconds:F1} s, window {window.Elapsed.TotalSeconds:F1} s, "
            + $"live Toke before {before}, after {after}, delta {after - before}");
        return after - before;
    }

    private void Report(StringBuilder log, string line)
    {
        log.AppendLine(line);
        output.WriteLine(line);
    }

    /// <summary>
    /// The redirector is the <c>git.exe</c> first on PATH (Git for Windows' <c>cmd\git.exe</c>); the
    /// real one is the sibling under <c>ucrt64\bin</c> or <c>mingw64\bin</c> of the same install.
    /// </summary>
    private static (string Redirector, string RealGit) LocateGit()
    {
        // The test host's own PATH can put an MSYS shell's ucrt64\bin first, which would hand back
        // the real git as "the redirector"; the redirector is the git.exe in a directory named cmd.
        string redirector = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"))
            .Where(directory => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), "cmd", StringComparison.OrdinalIgnoreCase))
            .Select(directory => Path.Combine(directory, "git.exe"))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Git for Windows' cmd git.exe redirector was not found on PATH or under Program Files.");

        string root = Path.GetDirectoryName(Path.GetDirectoryName(redirector))
            ?? throw new InvalidOperationException($"{redirector} has no install root above it.");
        string realGit = new[] { "ucrt64", "mingw64", "clangarm64" }
            .Select(flavour => Path.Combine(root, flavour, "bin", "git.exe"))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                $"The real git.exe must sit under ucrt64, mingw64 or clangarm64 next to the redirector at {redirector}.");
        return (redirector, realGit);
    }

    /// <summary>
    /// <c>NtQuerySystemInformation(SystemPoolTagInformation)</c>: the same counters
    /// <c>poolmon</c> shows. It needs an elevated process; the live count of a tag is its
    /// allocations minus its frees.
    /// </summary>
    private static class PoolTags
    {
        private const int SystemPoolTagInformation = 22;
        private const uint StatusInfoLengthMismatch = 0xC0000004;
        private const int TokeTag = 0x656B6F54;
        private const int EntrySize = 40;
        private const int FirstEntryOffset = 8;

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);

        public static long LiveTokens()
        {
            int length = 4 * 1024 * 1024;
            while (true)
            {
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    int status = NtQuerySystemInformation(SystemPoolTagInformation, buffer, length, out int needed);
                    if ((uint)status == StatusInfoLengthMismatch)
                    {
                        length = Math.Max(needed, length * 2);
                        continue;
                    }

                    if (status != 0)
                    {
                        throw new InvalidOperationException(
                            "NtQuerySystemInformation(SystemPoolTagInformation) failed with status 0x"
                            + ((uint)status).ToString("X8", CultureInfo.InvariantCulture)
                            + "; the pool-tag counters need an elevated process.");
                    }

                    int count = Marshal.ReadInt32(buffer, 0);
                    for (int index = 0; index < count; index++)
                    {
                        int offset = FirstEntryOffset + (index * EntrySize);
                        if (Marshal.ReadInt32(buffer, offset) == TokeTag)
                        {
                            return Marshal.ReadInt32(buffer, offset + 4) - Marshal.ReadInt32(buffer, offset + 8);
                        }
                    }

                    throw new InvalidOperationException("The pool-tag table has no Toke entry.");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
    }
}
