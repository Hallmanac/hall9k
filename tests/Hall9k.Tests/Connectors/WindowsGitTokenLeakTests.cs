using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
public sealed class WindowsGitTokenLeakCollection
{
    public const string Name = "WindowsGitTokenLeak";
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped off Windows, when Git for Windows' own layout
/// cannot be found, and when the kernel's pool-tag counters refuse to be read for lack of rights.
/// xunit 2.9.3 has no run-time skip, so the check is made here, once, when the test is discovered.
/// </summary>
public sealed class WindowsGitTokenLeakFactAttribute : FactAttribute
{
    public WindowsGitTokenLeakFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: it counts live access-token objects in the Windows kernel pool.";
        }
        else if (WindowsGitTokenLeakTests.LocateRealGit() is null)
        {
            Skip = "Needs Git for Windows' own layout: a git.exe under ucrt64\\bin, mingw64\\bin or clangarm64\\bin "
                + "of an install that has a cmd or bin directory on PATH or under Program Files.";
        }
        else if (!PoolTags.TryReadLiveTokens(out _, out uint status) && PoolTags.IsRefusalForLackOfRights(status))
        {
            Skip = "Needs an elevated process: reading the kernel's pool-tag counters was refused for lack of rights "
                + $"(status 0x{status.ToString("X8", CultureInfo.InvariantCulture)}). Run the tests from an elevated shell.";
        }
    }
}

/// <summary>
/// On this platform's Windows build, a launched program that itself starts a child and is still
/// running when that child exits leaves one kernel access token (pool tag <c>Toke</c>) allocated
/// until reboot. Git for Windows' launcher (<c>cmd\git.exe</c>, and <c>bin\git.exe</c>, the same
/// program) is exactly that: it starts the real <c>git.exe</c> and is still running when it
/// exits. h9kd runs git constantly, so the shared runner runs the real git instead.
/// <para>
/// Both arms start <c>git --version</c> through <see cref="ExternalProcess.Runner"/>, eight at a time.
/// The first arm starts the bare name <c>git</c>, which is the launcher whenever it is first on
/// PATH (the daemon's PATH on the node this was written on, and so its children's) unless the
/// runner has resolved it away. The second starts the real <c>git.exe</c> by a full path this test
/// finds for itself. The arms alternate in batches of equal wall-clock length, and which goes
/// first flips every round, because the arm that goes first in a pair reads high even when both
/// start the same program.
/// </para>
/// <para>
/// The test fails when the first arm leaves at least <see cref="FailureThreshold"/> more live
/// <c>Toke</c> tokens than the second over <see cref="LaunchesPerArm"/> launches each. The
/// threshold sits between what the noise reaches (three runs with the real git in both arms) and
/// what the leak reaches (the same test on the commit before the fix); the pull request records
/// both. The measured numbers are written to the test output.
/// </para>
/// </summary>
[Collection(WindowsGitTokenLeakCollection.Name)]
public sealed class WindowsGitTokenLeakTests(ITestOutputHelper output)
{
    private const int ChildrenPerBatch = 800;
    private const int Parallelism = 8;
    private const int Rounds = 8;
    private const int LaunchesPerArm = ChildrenPerBatch * Rounds;
    private const int WarmUpChildren = 20;

    /// <summary>How many launches whose output stayed held open the runner may report before the test calls it a fault rather than a blip.</summary>
    private const int StuckLaunchesTolerated = 5;

    private int _stuckLaunches;

    /// <summary>
    /// How many more live tokens the first arm may leave than the second. It sits between what
    /// the noise reaches and what the leak reaches at <see cref="LaunchesPerArm"/> launches: with
    /// the real git in both arms three runs differed by 304, 213 and -345, and before the fix the
    /// same test differed by 6168 (about one token a launch). It is at least three times the
    /// largest null difference and at most half the pre-fix one. The launch count was raised from
    /// 1,200 until that held, since at 1,200 the noise left no room for a threshold.
    /// </summary>
    private const int FailureThreshold = 1500;

    // Long enough for a batch of launches and for the tokens of the children that just exited to be
    // freed (observed up to about 5 s), and the same for both arms so the node's background
    // growth lands on both sides alike.
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(14);

    [WindowsGitTokenLeakFact]
    public async Task Starting_git_by_name_leaves_no_more_live_tokens_than_starting_the_real_git_directly()
    {
        string realGit = LocateRealGit()
            ?? throw new InvalidOperationException("The fact attribute skips this test when the real git.exe cannot be found.");
        const string byName = "git";
        string workingDirectory = Path.GetTempPath();
        output.WriteLine($"first git.exe on PATH: {FirstGitOnPath() ?? "(none)"}; real git by full path: {realGit}");

        // Not counted: the first batch of a run reads high (first-time image scans, JIT, the
        // daemon reacting to new children), which would land on whichever arm goes first.
        await MeasureBatchAsync("warm-up (not counted)", byName, workingDirectory, WarmUpChildren);
        await MeasureBatchAsync("warm-up (not counted)", realGit, workingDirectory, WarmUpChildren);

        long byNameTotal = 0;
        long realTotal = 0;
        for (int round = 1; round <= Rounds; round++)
        {
            long byNameDelta;
            long realDelta;
            if (round % 2 == 1)
            {
                byNameDelta = await MeasureBatchAsync($"round {round} by name", byName, workingDirectory);
                realDelta = await MeasureBatchAsync($"round {round} real git", realGit, workingDirectory);
            }
            else
            {
                realDelta = await MeasureBatchAsync($"round {round} real git", realGit, workingDirectory);
                byNameDelta = await MeasureBatchAsync($"round {round} by name", byName, workingDirectory);
            }

            byNameTotal += byNameDelta;
            realTotal += realDelta;
            output.WriteLine($"round {round}: by name +{byNameDelta}, real git +{realDelta}, difference {byNameDelta - realDelta}");
        }

        long difference = byNameTotal - realTotal;
        output.WriteLine($"TOTAL over {LaunchesPerArm} launches per arm: by name +{byNameTotal}, real git +{realTotal}, "
            + $"difference {difference} ({difference / (double)LaunchesPerArm:F2} per launch)");

        output.WriteLine($"launches whose output stayed held open after git exited 0: {_stuckLaunches}");
        _stuckLaunches.Should().BeLessThanOrEqualTo(
            StuckLaunchesTolerated,
            "a few launches may have their output held open on a loaded runner, but more than that is a fault in starting git");

        difference.Should().BeLessThan(
            FailureThreshold,
            $"starting git by name left {difference} more live Toke tokens than starting {realGit} directly across "
            + $"{LaunchesPerArm} launches each: the runner should start Git for Windows' real git, because its launcher "
            + "is still running when the real git exits and so keeps a kernel token alive for good");
    }

    private async Task<long> MeasureBatchAsync(string label, string git, string workingDirectory, int children = ChildrenPerBatch)
    {
        Stopwatch window = Stopwatch.StartNew();
        long before = PoolTags.ReadLiveTokens();
        int next = -1;
        await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(async _ =>
        {
            while (Interlocked.Increment(ref next) < children)
            {
                try
                {
                    ProcessResult result = await ExternalProcess.Runner(git, ["--version"], workingDirectory, CancellationToken.None);
                    result.ExitCode.Should().Be(0, $"{git} --version must succeed for the batch to mean anything");
                }
                catch (ProcessOutputStuckException stuck)
                {
                    // Seen once in about 13,000 launches on a loaded CI runner: git exited 0 and the
                    // runner's drain grace expired. The launch happened and its token is in the
                    // reading, so it still counts; only a run of them says something is wrong.
                    stuck.ExitCode.Should().Be(0, $"{git} --version must succeed for the batch to mean anything");
                    Interlocked.Increment(ref _stuckLaunches);
                }
            }
        }));

        TimeSpan launching = window.Elapsed;
        if (window.Elapsed < BatchWindow)
        {
            await Task.Delay(BatchWindow - window.Elapsed);
        }

        long after = PoolTags.ReadLiveTokens();
        output.WriteLine($"{label}: {children} launches in {launching.TotalSeconds:F1} s, window {window.Elapsed.TotalSeconds:F1} s, "
            + $"live Toke before {before}, after {after}, delta {after - before}");
        return after - before;
    }

    private static IEnumerable<string> Directories()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => directory.Trim().Trim('"'))
            .Append(Path.Combine(programFiles, "Git", "cmd"));
    }

    private static string? FirstGitOnPath() => Directories()
        .Select(directory => Path.Combine(directory, "git.exe"))
        .FirstOrDefault(File.Exists);

    /// <summary>
    /// The real git.exe, found from the PATH and Program Files by this test's own reading of
    /// Git for Windows' layout rather than by the code under test: the sibling under
    /// <c>ucrt64\bin</c>, <c>mingw64\bin</c> or <c>clangarm64\bin</c> of an install whose launcher
    /// is in a <c>cmd</c> or <c>bin</c> directory, or a git.exe already in one of those flavour
    /// directories.
    /// </summary>
    internal static string? LocateRealGit()
    {
        string[] flavours = ["ucrt64", "mingw64", "clangarm64"];
        foreach (string directory in Directories().Where(directory => File.Exists(Path.Combine(directory, "git.exe"))))
        {
            string trimmed = Path.TrimEndingDirectorySeparator(directory);
            string? parent = Path.GetDirectoryName(trimmed);
            string? grandparent = parent is null ? null : Path.GetDirectoryName(parent);
            if (grandparent is not null
                && string.Equals(Path.GetFileName(trimmed), "bin", StringComparison.OrdinalIgnoreCase)
                && flavours.Contains(Path.GetFileName(parent) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                return Path.Combine(trimmed, "git.exe");
            }

            string? root = parent;
            if (root is not null
                && (string.Equals(Path.GetFileName(trimmed), "cmd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(trimmed), "bin", StringComparison.OrdinalIgnoreCase))
                && flavours.Select(flavour => Path.Combine(root, flavour, "bin", "git.exe")).FirstOrDefault(File.Exists) is { } real)
            {
                return real;
            }
        }

        return null;
    }
}

/// <summary>
/// <c>NtQuerySystemInformation(SystemPoolTagInformation)</c>: the same counters <c>poolmon</c>
/// shows. The live count of a tag is its allocations minus its frees.
/// </summary>
internal static class PoolTags
{
    private const int SystemPoolTagInformation = 22;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusAccessDenied = 0xC0000022;
    private const uint StatusPrivilegeNotHeld = 0xC0000061;
    private const int TokeTag = 0x656B6F54;
    private const int EntrySize = 40;
    private const int FirstEntryOffset = 8;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);

    public static bool IsRefusalForLackOfRights(uint status) => status is StatusAccessDenied or StatusPrivilegeNotHeld;

    public static long ReadLiveTokens() => TryReadLiveTokens(out long live, out uint status)
        ? live
        : throw new InvalidOperationException(
            $"NtQuerySystemInformation(SystemPoolTagInformation) failed with status 0x{status.ToString("X8", CultureInfo.InvariantCulture)}.");

    /// <summary>False when the read failed; <paramref name="status"/> is then the NTSTATUS (0 when the table simply has no Toke entry).</summary>
    public static bool TryReadLiveTokens(out long live, out uint status)
    {
        live = 0;
        int length = 4 * 1024 * 1024;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                int result = NtQuerySystemInformation(SystemPoolTagInformation, buffer, length, out int needed);
                status = (uint)result;
                if (status == StatusInfoLengthMismatch)
                {
                    length = Math.Max(needed, length * 2);
                    continue;
                }

                if (status != 0)
                {
                    return false;
                }

                int count = Marshal.ReadInt32(buffer, 0);
                for (int index = 0; index < count; index++)
                {
                    int offset = FirstEntryOffset + (index * EntrySize);
                    if (Marshal.ReadInt32(buffer, offset) == TokeTag)
                    {
                        live = Marshal.ReadInt32(buffer, offset + 4) - Marshal.ReadInt32(buffer, offset + 8);
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
