using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace Hall9k.Tests.Connectors;

[CollectionDefinition("DetachedStartTokenLeak", DisableParallelization = true)]
public sealed class DetachedStartTokenLeakCollection;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless <c>HALL9K_DETACHED_START_REPRO=1</c>, off
/// Windows, on GitHub Actions, when Git for Windows' launcher cannot be found, and when the
/// kernel's pool-tag counters refuse to be read for lack of rights. It is opt-in because it is a
/// measurement taken for a spike (docs/spikes/h9kd-detached-console/FINDINGS.md), not a gate: it
/// takes about five minutes and reads a machine-wide kernel counter.
/// </summary>
public sealed class WindowsDetachedStartFactAttribute : FactAttribute
{
    public const string OptInVariable = "HALL9K_DETACHED_START_REPRO";

    public WindowsDetachedStartFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Opt-in: set {OptInVariable}=1 to run this five-minute kernel measurement (see docs/spikes/h9kd-detached-console).";
        }
        else if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: it counts live access-token objects in the Windows kernel pool.";
        }
        else if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Not run on GitHub Actions: a hosted runner's kernel-wide token counter is too noisy for this measurement.";
        }
        else if (WindowsDetachedStartTokenLeakTests.LocateLauncher() is null)
        {
            Skip = "Needs Git for Windows' own layout: a cmd\\git.exe launcher beside a ucrt64, mingw64 or clangarm64 real git.exe.";
        }
        else if (!PoolTags.TryReadLiveTokens(out _, out uint status) && PoolTags.IsRefusalForLackOfRights(status))
        {
            Skip = "Needs an elevated process: reading the kernel's pool-tag counters was refused for lack of rights "
                + $"(status 0x{status.ToString("X8", CultureInfo.InvariantCulture)}). Run the tests from an elevated shell.";
        }
    }
}

/// <summary>
/// Does starting a child with no console (<c>DETACHED_PROCESS</c>) stop the Windows kernel token
/// leak? On the build this was written on, no: a launched program that starts a child and is still
/// running when that child exits keeps one kernel access token (pool tag <c>Toke</c>) allocated until
/// reboot, and it does so whether or not it, or anything below it, has a console. Git for Windows'
/// launcher (<c>cmd\git.exe</c>) is such a program: it starts the real <c>git.exe</c> and waits.
/// <para>
/// Three arms, started one at a time with their output redirected: today's start of the launcher
/// (<see cref="ExternalProcess.Runner"/>: <c>Process.Start</c>, redirected output, console inherited),
/// a detached start of the same launcher (<c>CreateProcessW</c> with <c>DETACHED_PROCESS</c> and
/// redirected output pipes), and a null that starts a program with no child of its own (the real
/// <c>git.exe</c> by full path). Batches are of equal wall-clock length, and the order rotates and
/// reverses between rounds so no arm always goes first.
/// </para>
/// <para>
/// The assertions record what was found and fail the day that stops being true: the plain start and
/// the detached start must each leave at least <see cref="Threshold"/> more live tokens than the null.
/// A fix on the Windows side, or a start mode that avoids the leak, shows up here as a failure; read
/// the findings document before changing the threshold. The measured numbers are written to the
/// test output, and a kernel kept-token trace (docs/spikes/h9kd-detached-console) is the measure of
/// record, since a live count of a machine-wide counter carries noise of a few hundred tokens.
/// </para>
/// </summary>
[Collection("DetachedStartTokenLeak")]
public sealed class WindowsDetachedStartTokenLeakTests(ITestOutputHelper output)
{
    private const int LaunchesPerBatch = 200;
    private const int Rounds = 6;
    private const int WarmUpLaunches = 20;

    /// <summary>
    /// How many more live tokens an arm must leave than the null over <c>Rounds * LaunchesPerBatch</c>
    /// launches (1,200). The expected figure is about one token a launch (about 1,200); the noise at
    /// this size is a few hundred, so 400 sits well inside the gap.
    /// </summary>
    private const long Threshold = 400;

    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(16);

    private enum Arm
    {
        Plain,
        Detached,
        Null,
    }

    [WindowsDetachedStartFact]
    public async Task A_detached_start_of_git_for_windows_launcher_leaves_as_many_tokens_as_a_plain_start()
    {
        (string launcher, string realGit) = LocateLauncher()
            ?? throw new InvalidOperationException("The fact attribute skips this test when Git for Windows cannot be found.");
        string workingDirectory = Path.GetTempPath();
        output.WriteLine($"launcher {launcher}; real git {realGit}");

        foreach (Arm arm in Enum.GetValues<Arm>())
        {
            await MeasureBatchAsync(arm, launcher, realGit, workingDirectory, WarmUpLaunches, "warm-up (not counted)");
        }

        Dictionary<Arm, long> totals = Enum.GetValues<Arm>().ToDictionary(arm => arm, _ => 0L);
        for (int round = 1; round <= Rounds; round++)
        {
            Arm[] order = Enum.GetValues<Arm>();
            int rotation = round % order.Length;
            order = [.. order.Skip(rotation), .. order.Take(rotation)];
            if (round % 2 == 0)
            {
                Array.Reverse(order);
            }

            foreach (Arm arm in order)
            {
                totals[arm] += await MeasureBatchAsync(arm, launcher, realGit, workingDirectory, LaunchesPerBatch, $"round {round}");
            }
        }

        int launches = Rounds * LaunchesPerBatch;
        long plainExtra = totals[Arm.Plain] - totals[Arm.Null];
        long detachedExtra = totals[Arm.Detached] - totals[Arm.Null];
        output.WriteLine($"TOTAL over {launches} launches per arm: plain +{totals[Arm.Plain]}, detached +{totals[Arm.Detached]}, null +{totals[Arm.Null]}; "
            + $"plain minus null {plainExtra} ({plainExtra / (double)launches:F2} per launch), "
            + $"detached minus null {detachedExtra} ({detachedExtra / (double)launches:F2} per launch)");

        plainExtra.Should().BeGreaterThanOrEqualTo(
            Threshold,
            "today's start of a program that starts a child and outlives it should leave a kernel token; if it no longer does, "
            + "the Windows leak this spike measured is gone and the comparison below means nothing");
        detachedExtra.Should().BeGreaterThanOrEqualTo(
            Threshold,
            "a start with no console (DETACHED_PROCESS) was found not to stop the leak; if it now does, "
            + "docs/spikes/h9kd-detached-console/FINDINGS.md needs revisiting and the daemon's start mode is worth changing");
    }

    private async Task<long> MeasureBatchAsync(Arm arm, string launcher, string realGit, string workingDirectory, int launches, string label)
    {
        Stopwatch window = Stopwatch.StartNew();
        long before = PoolTags.ReadLiveTokens();
        for (int index = 0; index < launches; index++)
        {
            int exitCode = arm switch
            {
                Arm.Plain => (await ExternalProcess.Runner(launcher, ["--version"], workingDirectory, CancellationToken.None)).ExitCode,
                Arm.Null => (await ExternalProcess.Runner(realGit, ["--version"], workingDirectory, CancellationToken.None)).ExitCode,
                _ => DetachedStart.Run(launcher, "--version", workingDirectory),
            };
            exitCode.Should().Be(0, $"{arm} start of git --version must succeed for the batch to mean anything");
        }

        TimeSpan launching = window.Elapsed;
        if (window.Elapsed < BatchWindow)
        {
            await Task.Delay(BatchWindow - window.Elapsed);
        }

        long after = PoolTags.ReadLiveTokens();
        output.WriteLine($"{label} {arm}: {launches} launches in {launching.TotalSeconds:F1} s, window {window.Elapsed.TotalSeconds:F1} s, "
            + $"live Toke before {before}, after {after}, delta {after - before}");
        return after - before;
    }

    /// <summary>
    /// Git for Windows' launcher (<c>cmd\git.exe</c>) and the real <c>git.exe</c> it starts, found by
    /// this test's own reading of the layout: the install root is the parent of a <c>cmd</c> directory
    /// on PATH or under Program Files, and the real git sits under <c>ucrt64\bin</c>, <c>mingw64\bin</c>
    /// or <c>clangarm64\bin</c> of that root.
    /// </summary>
    internal static (string Launcher, string RealGit)? LocateLauncher()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] flavours = ["ucrt64", "mingw64", "clangarm64"];
        IEnumerable<string> directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => directory.Trim().Trim('"'))
            .Append(Path.Combine(programFiles, "Git", "cmd"));
        foreach (string directory in directories)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(directory);
            string launcher = Path.Combine(trimmed, "git.exe");
            string? root = Path.GetDirectoryName(trimmed);
            if (root is null
                || !string.Equals(Path.GetFileName(trimmed), "cmd", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(launcher))
            {
                continue;
            }

            string? real = flavours.Select(flavour => Path.Combine(root, flavour, "bin", "git.exe")).FirstOrDefault(File.Exists);
            if (real is not null)
            {
                return (launcher, real);
            }
        }

        return null;
    }
}

/// <summary>
/// Starts a program with <c>CreateProcessW</c> and <c>DETACHED_PROCESS</c>: the child gets no console,
/// its standard output and error are pipes this process reads, and its standard input is NUL. This is
/// the one start mode <see cref="ProcessStartInfo"/> cannot express.
/// </summary>
internal static class DetachedStart
{
    private const uint DetachedProcess = 0x00000008;
    private const int UseStandardHandles = 0x00000100;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint InfiniteWait = 0xFFFFFFFF;
    private const int HandleFlagInherit = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, ref SecurityAttributes attributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Runs the program and waits for it; its output is read and discarded. Returns its exit code.</summary>
    public static int Run(string fileName, string arguments, string workingDirectory)
    {
        SecurityAttributes inheritable = new() { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, ref inheritable, 0)
            || !CreatePipe(out SafeFileHandle errorRead, out SafeFileHandle errorWrite, ref inheritable, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        }

        using SafeFileHandle outputReadEnd = outputRead;
        using SafeFileHandle outputWriteEnd = outputWrite;
        using SafeFileHandle errorReadEnd = errorRead;
        using SafeFileHandle errorWriteEnd = errorWrite;
        SetHandleInformation(outputReadEnd, HandleFlagInherit, 0);
        SetHandleInformation(errorReadEnd, HandleFlagInherit, 0);
        using SafeFileHandle nul = CreateFileW("NUL", GenericRead, FileShareReadWrite, ref inheritable, OpenExisting, 0x80, IntPtr.Zero);

        StartupInfo startup = new()
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = UseStandardHandles,
            StandardInput = nul.DangerousGetHandle(),
            StandardOutput = outputWriteEnd.DangerousGetHandle(),
            StandardError = errorWriteEnd.DangerousGetHandle(),
        };
        StringBuilder commandLine = new($"\"{fileName}\" {arguments}");
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, DetachedProcess, IntPtr.Zero, workingDirectory, ref startup, out ProcessInformation created))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"CreateProcess({fileName}) failed with Win32 error {error}.");
        }

        CloseHandle(created.Thread);
        try
        {
            // The write ends must be closed here or the reads below never see end-of-file.
            outputWriteEnd.Dispose();
            errorWriteEnd.Dispose();
            using FileStream output = new(outputReadEnd, FileAccess.Read, 4096, false);
            using FileStream error = new(errorReadEnd, FileAccess.Read, 4096, false);
            Task drainOutput = output.CopyToAsync(Stream.Null);
            Task drainError = error.CopyToAsync(Stream.Null);
            WaitForSingleObject(created.Process, InfiniteWait);
            Task.WaitAll([drainOutput, drainError], TimeSpan.FromSeconds(5));
            return GetExitCodeProcess(created.Process, out uint exitCode) ? unchecked((int)exitCode) : -1;
        }
        finally
        {
            CloseHandle(created.Process);
        }
    }
}
