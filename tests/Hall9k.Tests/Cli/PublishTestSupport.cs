using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The `dotnet publish`-running machinery shared by <see cref="PublishExcludesDevelopmentSettingsTests"/>
/// and <see cref="PublishStampsInformationalVersionTests"/> — the only two tests in the suite that
/// exercise a real publish rather than mocking around it, and for the identical reason: each is a
/// regression net for an assumption only a real `dotnet publish` can actually prove (cycle 1
/// conformance review finding: the two classes had carried nearly the same ~85 lines independently).
/// <para>
/// Both callers sit in the <c>PublishesBinary</c> collection and carry the trait of the same
/// name; see <c>tests/Hall9k.Tests/README.md</c> for what that buys and why the serial lane is a
/// correctness requirement here rather than a throughput tweak.
/// </para>
/// </summary>
internal static class PublishTestSupport
{
    /// <summary>
    /// The wall-clock budget for one publish. Unchanged at five minutes from the version of this
    /// helper that kept missing it (the Windows full-suite baseline of 2026-09-08): raising the
    /// budget would only have let the same queued publish run longer, so what changed instead is
    /// how much work the publish does — no restore, and the repository's own already-warm
    /// <c>obj/Release</c> instead of a from-scratch intermediate tree per test (see
    /// <see cref="RunPublishAsync"/>). The budget stays as the backstop for the one failure it was
    /// always for: a publish blocked indefinitely on NuGet's machine-wide global-packages lock.
    /// </summary>
    internal static readonly TimeSpan PublishBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The process names counted for the load figure a budget miss reports. Exactly the family
    /// the 2026-09-08 baseline sampled (dotnet, testhost, vstest.console) plus the two build
    /// servers a publish actually queues behind — MSBuild's own worker nodes and the shared C#
    /// compiler — since those are what a reader of the failure needs to see to tell a loaded
    /// machine from a wedged publish. Matched without the Windows <c>.exe</c> suffix, which
    /// <see cref="Process.ProcessName"/> does not carry.
    /// </summary>
    private static readonly string[] DotnetFamilyProcessNames =
        ["dotnet", "testhost", "vstest.console", "MSBuild", "VBCSCompiler"];

    /// <summary>
    /// The configuration both tests publish in, named once because the kill path has to delete
    /// exactly the intermediate directory the publish was writing (see
    /// <see cref="DiscardKilledPublishIntermediates"/>).
    /// </summary>
    private const string PublishConfiguration = "Release";

    /// <summary>How much of a killed publish's own output the failure message carries.</summary>
    private const int PublishOutputTailLength = 1000;

    /// <summary>How long a killed publish's pipes get to close before the drain gives up.</summary>
    private static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(10);

    internal sealed record ExecResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;
    }

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "Hall9k.slnx")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException($"No Hall9k.slnx found above {AppContext.BaseDirectory}.");
    }

    internal static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Runs `dotnet publish` directly rather than through the shared Exec.RunAsync, because on a
    /// budget miss the caller must kill the whole process tree, not just the `dotnet` parent:
    /// `dotnet publish` hands off to MSBuild worker nodes that outlive a bare WaitForExitAsync
    /// cancellation and keep the NuGet global-packages lock held, which would otherwise wedge the
    /// NEXT run of this test (or a concurrent `dotnet publish` anywhere else on the machine)
    /// behind the same lock this one was trying to escape.
    /// <para>
    /// Three flags carry the whole of why this now finishes under full-suite load, where the
    /// earlier version cancelled at five minutes on a 20-core Windows node with fifteen dotnet
    /// processes and four Postgres containers alive beside it:
    /// </para>
    /// <para>
    /// <c>--no-restore</c>. The publish restored on every run, and a restore is the one step here
    /// that can reach the network: PR #274's ubuntu leg failed this pair on a socket wait, and the
    /// Mac's 2026-09-07 21:25 EDT failure was the same shape. Nothing in either test is about
    /// restore, and nothing needs one — the `dotnet build` (or `dotnet test`'s own implicit build)
    /// that necessarily precedes any run of this suite has already written
    /// <c>src/Hall9k.*/obj/project.assets.json</c>, which is configuration-independent, so a
    /// Debug build satisfies this Release publish. Only a caller that deliberately runs
    /// `dotnet test --no-build --no-restore` against a wiped <c>obj/</c> could reach a missing
    /// assets file, and that caller has no test binary to run either.
    /// </para>
    /// <para>
    /// No intermediate-output redirection. The earlier version sent every project's obj and bin
    /// into a fresh temp tree per test (UseArtifactsOutput/ArtifactsPath) for isolation, which
    /// meant each test compiled the daemon's whole project graph from scratch and — fatally for
    /// <c>--no-restore</c> — looked for its assets file in a directory no restore had ever written
    /// (NETSDK1004). Publishing against the repository's own <c>obj/Release</c> instead reuses the
    /// build cache the suite's own preceding build already warmed, and shares it between the two
    /// tests. What that gives up is isolation from another MSBuild touching the same project
    /// directories, which the collection below buys back in-process; across processes, two
    /// concurrent `dotnet test` runs out of one worktree already raced each other's ordinary
    /// build output before this change, and a Release-configuration publish still cannot collide
    /// with the Debug tree a concurrent dev-loop build writes. What it also gives up is a kill
    /// landing harmlessly in a throwaway directory, which
    /// <see cref="DiscardKilledPublishIntermediates"/> answers for. Measured on the Windows node
    /// inside a full `dotnet test` run: 4.7s and 7.6s, against the 300s budget both tests used to
    /// exhaust. The two are not equal because a global <c>InformationalVersion</c> override is
    /// exactly the kind of property change that invalidates the shared cache, so whichever of the
    /// pair runs second pays to put the other's version stamp back — still an order of magnitude
    /// inside the budget, and the reason the sharing is of the cache rather than of one published
    /// output.
    /// </para>
    /// <para>
    /// <c>-nodeReuse:false</c>. MSBuild otherwise leaves its worker nodes alive for fifteen
    /// minutes after the publish, holding memory through the rest of the suite on a node the
    /// baseline measured down to 3.9 GB free — and a reused node is exactly what two concurrent
    /// gates on one Windows machine crashed in each other in the incident behind
    /// <see cref="Hall9k.Daemon.Execution.GateInfrastructureFailureClassifier"/>'s MSB4166
    /// marker. Fresh nodes cost this publish well under a second.
    /// </para>
    /// <para>
    /// The budget is owned here rather than passed in, so the one place that knows a miss happened
    /// is the place that reports it with the load it saw. It is deliberately the only cancellation
    /// source: xUnit 2.9 hands a test no ambient token to compose with, so a
    /// <c>CancellationToken</c> parameter here could only ever receive
    /// <see cref="CancellationToken.None"/> — nothing a caller holds is being dropped.
    /// </para>
    /// </summary>
    internal static async Task<ExecResult> RunPublishAsync(
        string repoRoot,
        string project,
        string outputDirectory,
        IReadOnlyList<string> extraProperties)
    {
        using CancellationTokenSource budget = new(PublishBudget);
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        List<string> arguments =
        [
            "publish", Path.Combine(repoRoot, "src", project),
            "-c", PublishConfiguration, "-o", outputDirectory, "--nologo",
            "--no-restore", "-nodeReuse:false",
            .. extraProperties,
        ];
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        long startedAt = Stopwatch.GetTimestamp();
        process.Start();

        // Read without the budget's token: on a miss these two tasks are what the failure message
        // quotes the publish's own last output from, and a reader cancelled along with the wait
        // would have thrown that output away instead.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            // Both figures are taken before anything is torn down, so neither carries the cost of
            // the teardown itself: the miss happened when the budget fired, not ten seconds later
            // once the pipes had drained.
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
            KillProcessTree(process);
            string load = DescribeDotnetFamilyLoad();

            // Drain before discarding, not after. Process.Kill only *requests* termination — the
            // tree is still dying when it returns — and these reader tasks completing is the one
            // observable signal in hand that a compiler mid-write on obj/Release has actually
            // gone, since the stdout handle it inherited closes when it does. Discarding first
            // raced those handles, and TryDelete swallows the IOException that race throws, so
            // the truncated assembly DiscardKilledPublishIntermediates exists to remove would
            // have survived on exactly the loaded node this path is for (independent pre-PR
            // review, cycle 1). A drain that gives up on its grace still falls through to the
            // discard: a best-effort delete of a tree that may still be held open beats leaving a
            // known-corrupt intermediate in place untried.
            string publishOutputTail = await DrainAsync(standardOutput, standardError);
            DiscardKilledPublishIntermediates(repoRoot);
            throw PublishBudgetExceededException.ForMiss(
                project,
                PublishBudget,
                elapsed,
                load,
                publishOutputTail);
        }

        return new ExecResult(process.ExitCode, await standardOutput, await standardError);
    }

    /// <summary>
    /// The kill is best-effort for the same reason the callers' <c>Dispose</c> deletes are: the
    /// process can exit in the window between the budget firing and this call, and an exception
    /// escaping here would replace the timeout the test actually hit with an unrelated failure —
    /// losing the load figures and the infrastructure classification that are the whole point of
    /// the throw below, and skipping <see cref="DiscardKilledPublishIntermediates"/> on the way
    /// out.
    /// <para>
    /// The filter is deliberately unbounded rather than the list of types
    /// <c>Process.Kill(entireProcessTree: true)</c> documents, because the two likeliest on this
    /// path were exactly the two the enumeration it replaces had missed (independent pre-PR
    /// review, cycle 1): <see cref="System.ComponentModel.Win32Exception"/> when the associated
    /// process cannot be terminated, and <see cref="AggregateException"/> wrapping one per
    /// descendant when only part of the tree could be — which is what a thrashing node racing its
    /// own MSBuild worker teardown produces. Nothing here is recoverable, and the contract is
    /// that nothing escaping here may replace the timeout report, so naming types would only
    /// leave the next unnamed one free to break that contract again.
    /// </para>
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Whatever it was, the timeout stays the reported failure; see above.
        }
    }

    /// <summary>
    /// Throws away the <c>obj/Release</c> tree of every project under <c>src/</c> after a killed
    /// publish. This is the one cost of publishing against the repository's own intermediates
    /// rather than a private tree: a compiler killed mid-write can leave a truncated assembly in
    /// <c>obj/Release</c> carrying a timestamp newer than its sources, which MSBuild's own
    /// up-to-date check then trusts — so the next ordinary build would happily copy a corrupt
    /// binary forward and fail somewhere with no connection to the publish that caused it. The
    /// intermediates are regenerable build output, so discarding them costs one recompile;
    /// <c>project.assets.json</c> and the NuGet-generated imports sit in <c>obj/</c> itself rather
    /// than under the configuration, so what <c>--no-restore</c> depends on is untouched. Runs
    /// only on the kill path, best-effort for the reason the callers' own Dispose deletes are
    /// (a file still held open a moment after Kill returns must not replace the timeout as the
    /// reported failure).
    /// </summary>
    private static void DiscardKilledPublishIntermediates(string repoRoot)
    {
        string sourceDirectory = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (string projectDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            TryDelete(Path.Combine(projectDirectory, "obj", PublishConfiguration));
        }
    }

    /// <summary>
    /// The tail of a killed publish's own output, or an honest statement that it was not
    /// captured — never a guess, which is why the two ways it can go uncaptured are reported
    /// separately: pipes still open when the grace ran out, and a read that ended on the broken
    /// pipe without producing output. The pipes normally close the moment the tree dies; a process
    /// that somehow survives the kill would otherwise leave this awaiting its own output forever,
    /// on top of the timeout it already hit. Awaiting this is also what orders the caller's
    /// intermediates discard behind the dying tree — see the budget-miss path in
    /// <see cref="RunPublishAsync"/>.
    /// </summary>
    private static async Task<string> DrainAsync(Task<string> standardOutput, Task<string> standardError)
    {
        Task both = Task.WhenAll(standardOutput, standardError);
        await Task.WhenAny(both, Task.Delay(OutputDrainGrace));
        if (!both.IsCompleted)
        {
            return $"(not captured: the killed publish's pipes had not closed "
                + $"{OutputDrainGrace.TotalSeconds:0}s after the kill)";
        }

        if (!both.IsCompletedSuccessfully)
        {
            // Finished, but with nothing to show: a read faulted on the broken pipe (or, were
            // these readers ever handed a token, was cancelled). Reported as what it was rather
            // than as the still-open pipe above, which would send a reader hunting a surviving
            // process that had in fact exited well inside the grace — a message asserting
            // something nobody observed (independent pre-PR review, cycle 1).
            string cause = both.Exception is { } failure
                ? string.Join(", ", failure.InnerExceptions.Select(inner => inner.GetType().Name))
                : "the read was cancelled";
            return $"(not captured: reading the killed publish's output failed — {cause})";
        }

        string combined = ((await standardOutput) + await standardError).Trim();
        // The ellipsis marks the cut, so a tail that starts mid-path reads as a truncation rather
        // than as a publish that somehow began there.
        return combined.Length <= PublishOutputTailLength
            ? combined
            : "…" + combined[^PublishOutputTailLength..];
    }

    /// <summary>
    /// The dotnet-family process count at the moment of a miss, broken down by name: the figure
    /// that tells a reader of the failure whether the publish was queued behind a busy suite (the
    /// 2026-09-08 baseline saw up to fifteen) or was alone on the machine and genuinely stuck.
    /// Enumeration itself is best-effort, and the outer filter is deliberately unbounded for the
    /// same reason <see cref="KillProcessTree"/>'s is: this figure is gathered to explain a
    /// timeout the test has already hit, so whatever the platform throws while listing processes
    /// — the exit of one mid-enumeration, a refusal to inspect another
    /// (<see cref="System.ComponentModel.Win32Exception"/>, which the earlier named-type filter
    /// had missed — Copilot review, PR #286), an unsupported listing outright — is reported as an
    /// unmeasured load rather than allowed to replace that timeout and take its marker and
    /// diagnostics with it. Naming types here would only leave the next unnamed one free to break
    /// that contract again.
    /// </summary>
    private static string DescribeDotnetFamilyLoad()
    {
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                    {
                        // Exited between the listing and this read, or is a process this one may
                        // not inspect. Either way its name is unknown, so it cannot be counted as
                        // dotnet-family load; skipping it keeps the rest of the figure measured
                        // rather than falling through to the "not measured" catch below.
                        continue;
                    }

                    if (DotnetFamilyProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            // Unbounded on purpose; see the summary above.
            return $"not measured ({exception.GetType().Name} enumerating processes)";
        }

        int total = counts.Values.Sum();
        string breakdown = counts.Count == 0
            ? "none alive"
            // Ordered case-insensitively so the breakdown reads alphabetically to a human rather
            // than putting every capitalised process name ahead of every lowercase one.
            : string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{entry.Key} {entry.Value}"));
        return $"{total} dotnet-family processes ({breakdown})";
    }

    internal static void AssertSucceeded(this ExecResult result)
    {
        result.Succeeded.Should().BeTrue(
            $"dotnet publish should succeed:\n{result.StandardOutput}\n{result.StandardError}");
    }
}
