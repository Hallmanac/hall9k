using System.Diagnostics;
using System.Globalization;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The fixed, machine-wide directory the test project's own <c>CrossProcessContainerGate</c> (in
/// <c>Hall9k.Tests</c>) polls for Postgres container permits (Decisions Log #132), together with
/// the holder-sidecar contract it writes beside each permit slot it holds, and the read this
/// class does over both at a killed gate's own timeout diagnostic. Defined here rather than in the
/// test project, for the same reason <see cref="GateInfrastructureFailureClassifier.GateWaitEvidenceDirectoryEnvironmentVariable"/>
/// is: this is the reader, and <c>Hall9k.Tests</c> already references <c>Hall9k.Daemon</c> — the
/// reverse direction is what AGENTS.md's reference graph forbids — so the writer reads this same
/// contract rather than duplicating its literal value or its format (task: host-coupled suites run
/// only in the node's serialized host gate; a killed gate names who held the permits).
/// </summary>
public static class ContainerGateDirectory
{
    /// <summary>
    /// Deliberately the identical resolution <c>CrossProcessContainerGate</c>'s own former
    /// private <c>ResolveGateDirectory</c> used: fixed and machine-wide, so every <c>dotnet
    /// test</c> process, whatever repository or worktree it runs from, contends for — and is
    /// diagnosed from — the identical directory. <c>/tmp</c> on Unix rather than
    /// <see cref="Path.GetTempPath"/>, for the identical <c>$TMPDIR</c>-ambiguity reason that
    /// method's own comment covered: two processes on the same machine can otherwise resolve two
    /// different temp roots and silently double the bound this gate exists to enforce.
    /// </summary>
    public static string Resolve()
    {
        string root = OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp";
        return Path.Combine(root, "hall9k-postgres-container-gate");
    }

    /// <summary>A holder sidecar's own file suffix, beside a permit slot file (e.g. <c>permit-0.lock.holder</c>).</summary>
    public const string SidecarSuffix = ".holder";

    /// <summary>
    /// Whether the process a wait file or a holder sidecar names is still alive on this machine —
    /// the one liveness question both the dead-waiter sweep and a killed gate's own stale-holder
    /// read share, real by default (<see cref="IsProcessAlive"/>) and replaceable by a fake probe
    /// in a test, so neither has to spawn or kill a real process to prove its own logic.
    /// </summary>
    public delegate bool LivenessProbe(int processId, DateTimeOffset? startedAtUtc);

    /// <summary>
    /// The real liveness check: a process with this id exists on this machine, and, when a start
    /// time is also given (a holder sidecar always carries one; a bare wait file's filename does
    /// not), its actual start time is close enough to rule out an unrelated process that reused
    /// the same pid since the wait or the hold was recorded.
    /// </summary>
    public static bool IsProcessAlive(int processId, DateTimeOffset? startedAtUtc)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return startedAtUtc is null
                || StartTimesAreCloseEnough(process.StartTime.ToUniversalTime(), startedAtUtc.Value);
        }
        catch (ArgumentException)
        {
            // No such process — Process.GetProcessById's own documented contract for a pid that
            // does not currently exist on this machine.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between GetProcessById locating it and this call reading its
            // own start time back.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The OS denied this account access to the process's start time — routine once a
            // recorded pid is reused by a process this account cannot query (a Windows service
            // running as SYSTEM, most often). GetProcessById already proved a process with this
            // pid exists, so this is "can't confirm it's the same one", not "it's gone" — treat
            // it as alive rather than let this best-effort read crash the timeout diagnostic it
            // decorates (VerificationRunner's own gate-kill handling has no catch-all above it).
            return true;
        }
    }

    // A few seconds of slack for ordinary clock-resolution rounding between the moment a holder
    // recorded its own start time and the moment this check re-reads the OS for it — not a
    // pid-reuse race window, which no fixed slack here could meaningfully bound anyway.
    private static bool StartTimesAreCloseEnough(DateTime observedUtc, DateTimeOffset recordedUtc) =>
        Math.Abs((observedUtc - recordedUtc.UtcDateTime).TotalSeconds) < 5;

    /// <summary>One acquirer's own sidecar content, beside its permit slot file.</summary>
    public static string FormatSidecar(
        int processId, DateTimeOffset processStartTimeUtc, string workingDirectory, DateTimeOffset acquiredAtUtc) =>
        $"ProcessId: {processId.ToString(CultureInfo.InvariantCulture)}\n"
        + $"ProcessStartTimeUtc: {processStartTimeUtc.UtcDateTime:o}\n"
        + $"WorkingDirectory: {workingDirectory}\n"
        + $"AcquiredAtUtc: {acquiredAtUtc.UtcDateTime:o}\n";

    /// <summary>
    /// A sidecar's own parsed fields — null for a field this content did not carry or could not
    /// parse, read honestly rather than guessed (AGENTS.md: never guess at unobserved facts).
    /// </summary>
    public readonly record struct SidecarHolder(
        int? ProcessId, DateTimeOffset? ProcessStartTimeUtc, string? WorkingDirectory, DateTimeOffset? AcquiredAtUtc);

    /// <summary>Parses <see cref="FormatSidecar"/>'s own shape back out — tolerant of an unknown or missing line, never throwing on malformed content.</summary>
    public static SidecarHolder ParseSidecar(string content)
    {
        int? processId = null;
        DateTimeOffset? processStartTimeUtc = null;
        string? workingDirectory = null;
        DateTimeOffset? acquiredAtUtc = null;

        foreach (string line in content.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "ProcessId"
                    when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id):
                    processId = id;
                    break;
                case "ProcessStartTimeUtc"
                    when DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset started):
                    processStartTimeUtc = started;
                    break;
                case "WorkingDirectory":
                    workingDirectory = value;
                    break;
                case "AcquiredAtUtc"
                    when DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset acquired):
                    acquiredAtUtc = acquired;
                    break;
            }
        }

        return new SidecarHolder(processId, processStartTimeUtc, workingDirectory, acquiredAtUtc);
    }

    /// <summary>
    /// A wait file's own embedded process id — <c>waiting-{pid}-{guid}.txt</c>,
    /// <c>CrossProcessContainerGate.AcquireAsync</c>'s own naming — parsed here rather than a
    /// second time in the test project so the writer's filename shape and this reader's own
    /// parse of it can never drift apart.
    /// </summary>
    public static bool TryExtractWaiterProcessId(string fileName, out int processId)
    {
        int firstDash = fileName.IndexOf('-');
        int secondDash = firstDash < 0 ? -1 : fileName.IndexOf('-', firstDash + 1);
        if (firstDash < 0 || secondDash < 0)
        {
            processId = 0;
            return false;
        }

        return int.TryParse(
            fileName[(firstDash + 1)..secondDash], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
    }

    /// <summary>
    /// A short, human-readable listing of every wait file and holder sidecar
    /// <paramref name="gateDirectory"/> held at the moment this is called — evidence for a human
    /// reading a killed gate's own failure text naming who held the permits, never itself part of
    /// what classifies the kill as infrastructure (<see cref="GateInfrastructureFailureClassifier"/>
    /// keeps deciding that from the gate's own captured output and the per-run wait-evidence
    /// directory, exactly as before). Null when the directory does not exist or holds neither kind
    /// of file — the ordinary case, since most kills are not contention-related at all.
    /// </summary>
    public static string? DescribeContents(string gateDirectory, LivenessProbe? livenessProbe = null)
    {
        if (!Directory.Exists(gateDirectory))
        {
            return null;
        }

        LivenessProbe isAlive = livenessProbe ?? IsProcessAlive;

        List<string> waiters = [];
        List<string> holders = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(gateDirectory, "waiting-*.txt"))
            {
                string name = Path.GetFileName(path);
                waiters.Add(TryExtractWaiterProcessId(name, out int pid)
                    ? $"{name} (pid {pid.ToString(CultureInfo.InvariantCulture)}{(isAlive(pid, null) ? "" : ", stale")})"
                    : name);
            }

            foreach (string path in Directory.EnumerateFiles(gateDirectory, $"permit-*{SidecarSuffix}"))
            {
                holders.Add(DescribeHolder(Path.GetFileName(path), TryReadSidecar(path), isAlive));
            }
        }
        catch (IOException)
        {
            // The directory itself vanished mid-enumeration (a temp-directory reaper racing this
            // best-effort read, the same race CrossProcessContainerGate.TryOpen's own
            // DirectoryNotFoundException handling already documents) — this listing is evidence
            // for a human, never something a killed gate's own classification depends on, so a
            // lost read here degrades to "nothing to report" rather than throwing out of
            // VerificationRunner's own timeout handler.
            return null;
        }

        if (waiters.Count == 0 && holders.Count == 0)
        {
            return null;
        }

        waiters.Sort(StringComparer.Ordinal);
        holders.Sort(StringComparer.Ordinal);

        List<string> parts = [];
        if (waiters.Count > 0)
        {
            parts.Add($"{waiters.Count.ToString(CultureInfo.InvariantCulture)} wait file(s) [{FormatEntries(waiters)}]");
        }

        if (holders.Count > 0)
        {
            parts.Add($"{holders.Count.ToString(CultureInfo.InvariantCulture)} holder sidecar(s) [{FormatEntries(holders)}]");
        }

        return $"The shared container gate directory ({gateDirectory}) held {string.Join(" and ", parts)} at the kill.";
    }

    // A short, human-readable listing, per this method's own doc comment, stays short even when
    // the directory itself is not: the origin incident found 25 stale wait files in one directory,
    // each roughly 60 characters once described, and nothing else here bounds how many
    // accumulate between sweeps. Truncated after the first handful, with a "+N more" tail, so the
    // evidence a killed gate's failure reason carries (and VerificationRunner persists on the
    // event) stays proportionate to what a human actually needs to see rather than growing with
    // however long it has been since some other process last triggered a sweep.
    private const int MaxDescribedEntries = 8;

    private static string FormatEntries(IReadOnlyList<string> entries)
    {
        if (entries.Count <= MaxDescribedEntries)
        {
            return string.Join("; ", entries);
        }

        int omitted = entries.Count - MaxDescribedEntries;
        return string.Join("; ", entries.Take(MaxDescribedEntries))
            + $"; +{omitted.ToString(CultureInfo.InvariantCulture)} more";
    }

    private static string DescribeHolder(string sidecarName, string? content, LivenessProbe isAlive)
    {
        if (content is null)
        {
            return sidecarName;
        }

        SidecarHolder holder = ParseSidecar(content);
        if (holder.ProcessId is not { } pid)
        {
            return sidecarName;
        }

        bool alive = isAlive(pid, holder.ProcessStartTimeUtc);
        string describedProcess = $"pid {pid.ToString(CultureInfo.InvariantCulture)}{(alive ? "" : ", stale")}";
        string describedWorkingDirectory = holder.WorkingDirectory is { } workingDirectory
            ? $" in {workingDirectory}"
            : string.Empty;
        return $"{sidecarName} ({describedProcess}{describedWorkingDirectory})";
    }

    private static string? TryReadSidecar(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            // The holder released its permit (and deleted this sidecar) between this call's own
            // enumeration and this read — the same benign race UnresolvedGateWaitExcerpt already
            // tolerates for a wait-evidence file, and never a reason to fail this best-effort
            // listing.
            return null;
        }
    }
}
