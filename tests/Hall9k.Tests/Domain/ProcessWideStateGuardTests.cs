using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Three pieces of state in this process belong to no single test, and two of the three have
/// already produced a failure that read as a product bug: the two <see cref="Console"/> writers,
/// <c>Spectre.Console.AnsiConsole.Console</c>, and the ambient
/// <see cref="System.Globalization.CultureInfo"/>. xUnit runs distinct collections in parallel
/// inside one process, so a test that swaps any of them is deciding what every other test running
/// at that moment sees. This guard fails the build for any file in the test tree that mutates one
/// directly, naming the <c>TestSupport</c> helper that scopes it instead — the same mechanical
/// enforcement <see cref="HomeEnvironmentIsolationTests"/> gives the platform home, rather than
/// trusting every future test author to remember a rule two incidents and a near miss deep.
/// <para>
/// The incidents, all in one week (PLAN.md §16 #220): a captured
/// <see cref="Console.Error"/> holding <c>PostgresFixture</c>'s container-gate wait notice, written
/// by a class the capturing test had never heard of (2026-09-17 03:25 EDT, run 01a0ad80); a
/// captured <c>AnsiConsole</c> holding <c>StatusCommandMergedWithoutCopilotReviewTests</c>' rows
/// while <c>ToolDoctorTests</c> asserted on the doctor's own output (2026-09-17 11:15 EDT, run
/// 01a0af3c); and the culture case, which had not failed yet and, on the two flowing properties
/// alone, would not have — <c>CurrentCulture</c>'s setter writes an <see cref="AsyncLocal{T}"/>
/// whose change callback keeps the thread-static copy in step, so it flows across an <c>await</c>
/// and unwinds again with the pool thread's <see cref="ExecutionContext"/>. It is guarded here
/// because the same marker covers <c>DefaultThreadCurrentCulture</c>, which is process-wide with
/// nothing to unwind it at all, and because a scoped helper is the one spelling of this that cannot
/// be written with the restore omitted or the original captured wrongly
/// (<see cref="TestSupport.CultureScope"/> carries the full reasoning).
/// </para>
/// <para>
/// A source scan rather than a runtime check, for <see cref="ContainerRoutingGuardTests"/>'s
/// reason: the failure is "somebody assigned a static", which nothing this process could intercept
/// at test time. Matching runs against comment/string-stripped source
/// (<see cref="TestSourceTree.StripCommentsAndStrings"/>), so prose quoting a pattern — this file's
/// own marker list first of all — is never mistaken for real code, and each marker carries a
/// positive control asserting its own owning helper still matches it, so a scan that has gone dark
/// fails here instead of passing green while protecting nothing.
/// </para>
/// <para>
/// What it does not cover: a helper of some future test's own that wraps one of these mutations
/// would be flagged (correctly) at the helper, and every caller of that helper would then carry no
/// matching text at all — the shared-helper blind spot
/// <see cref="HomeEnvironmentIsolationTests"/> documents for its own scan, in a different shape.
/// Nor can it follow a mutation reached through a local — <c>Thread current = Thread.CurrentThread;
/// current.CurrentCulture = …</c> — which no source scan could; the two spellings that actually
/// get written, <c>CultureInfo.CurrentCulture</c> and <c>Thread.CurrentThread.CurrentCulture</c>,
/// are both matched directly, and each marker carries the spellings it must and must not flag.
/// One adjacent spelling is left unmarked on purpose: <c>AnsiConsole.Profile.Width = …</c> would
/// mutate the shared default console's own profile, but no file in this repository does it — every
/// <c>Profile.Width</c> assignment here is on a console the caller just built with
/// <c>AnsiConsole.Create</c> — and a marker whose owning helper performs nothing cannot carry the
/// positive control this class insists on. Add it the day a site appears.
/// The scan also says nothing about the platform home or the environment: that surface is
/// <see cref="HomeEnvironmentIsolationTests"/>' own, and its answer there is a shared serial
/// collection rather than a scoping helper, because an environment variable a production code path
/// reads on every call has nowhere per-test to be scoped to.
/// </para>
/// </summary>
public sealed class ProcessWideStateGuardTests
{
    /// <summary>
    /// One process-wide mutation, the helper that owns it, and the sentence an offending author
    /// needs to read. <see cref="Pattern"/> is a regex rather than a substring because two of the
    /// three are assignments to a property whose reads are perfectly legitimate
    /// (<c>AnsiConsole.Console.Profile</c>, <c>CultureInfo.CurrentCulture.Name</c>), so only the
    /// <c>=</c> form may be matched — and the trailing exclusion of a second <c>=</c> keeps a
    /// comparison from reading as an assignment. <see cref="Controls"/> and
    /// <see cref="NotControls"/> are the spellings that marker must and must not flag: the owning
    /// helper below proves one spelling still matches real code, but a pattern covering several
    /// spellings has alternatives its owner never exercises, and a stale one of those would go dark
    /// in silence. They are string literals on purpose, so the offenders scan's own stripper removes
    /// them before it reads this file.
    /// </summary>
    private sealed record ProcessWideMutation(
        string Name,
        Regex Pattern,
        string OwnerRelativePath,
        string Instead,
        string[] Controls,
        string[] NotControls);

    private static readonly ProcessWideMutation[] Mutations =
    [
        new(
            "a Console.Out/Console.Error/Console.In redirect",
            new Regex(@"Console\.Set(Out|Error|In)\s*\(", RegexOptions.Compiled),
            Path.Combine("Hall9k.Tests", "TestSupport", "ScopedConsoleCapture.cs"),
            "for the two output streams, use ScopedConsoleCapture.StandardError() or "
            + "ScopedConsoleCapture.StandardOutput(), which capture only what the capturing test's own "
            + "async flow wrote; for stdin there is no scoping helper, because nothing in this "
            + "repository reads stdin, and the helper named here is the only exemption this guard has — "
            + "so a test that needs stdin adds its own scoping helper under TestSupport and gives stdin "
            + "a marker of its own here, owned by that helper, rather than scoping the redirect in "
            + "place and expecting this scan to overlook it",
            Controls: ["Console.SetOut(writer);", "Console.SetError(writer);", "Console.SetIn(reader);"],
            NotControls: ["Console.Error.WriteLine(line);"]),
        new(
            "an AnsiConsole.Console swap",
            new Regex(@"AnsiConsole\.Console\s*=[^=]", RegexOptions.Compiled),
            Path.Combine("Hall9k.Tests", "TestSupport", "ScopedAnsiConsoleCapture.cs"),
            "use ScopedAnsiConsoleCapture.CaptureAsync(...) or ScopedAnsiConsoleCapture.Capture(...), which "
            + "route each render to the console belonging to the rendering flow's own scope",
            Controls: ["AnsiConsole.Console = console;"],
            NotControls: ["AnsiConsole.Console.Profile.Width", "if (AnsiConsole.Console == console)"]),
        new(
            "an ambient culture assignment",
            new Regex(
                @"(?:CultureInfo\.(?:CurrentCulture|CurrentUICulture|DefaultThreadCurrentCulture|DefaultThreadCurrentUICulture)"
                + @"|Thread\.CurrentThread\.Current(?:UI)?Culture)\s*=[^=]",
                RegexOptions.Compiled),
            Path.Combine("Hall9k.Tests", "TestSupport", "CultureScope.cs"),
            "use CultureScope.Run(...) or CultureScope.RunToCompletion(...), which set the culture on a "
            + "thread of the case's own so it cannot outlive the case. Thread.CurrentThread.CurrentCulture "
            + "is matched beside CultureInfo.CurrentCulture because on this runtime it is the same setter "
            + "spelled differently, and DefaultThreadCurrentCulture beside both because it is the one of "
            + "the four with no execution-context flow to unwind it",
            Controls:
            [
                "CultureInfo.CurrentCulture = named;",
                "CultureInfo.CurrentUICulture = named;",
                "CultureInfo.DefaultThreadCurrentCulture = named;",
                "CultureInfo.DefaultThreadCurrentUICulture = named;",
                "Thread.CurrentThread.CurrentCulture = named;",
                "Thread.CurrentThread.CurrentUICulture = named;",
            ],
            NotControls:
            [
                "CultureInfo.CurrentCulture.Name",
                "Thread.CurrentThread.CurrentCulture.Name",
                "if (CultureInfo.CurrentCulture == CultureInfo.InvariantCulture)",
            ]),
    ];

    [Fact]
    public void No_test_file_mutates_process_wide_console_or_culture_state_outside_its_scoping_helper()
    {
        // The whole tests/ directory, not this project alone: a second test project (this
        // repository has one, Hall9k.Tests.LockHolder) shares the same process-wide state the
        // moment it grows a test of its own — the same widening ContainerRoutingGuardTests and
        // ProcessTerminationGuardTests already settled on.
        string repositoryRoot = Path.GetDirectoryName(TestSourceTree.SourceDirectory())
            ?? throw new InvalidOperationException("the resolved src directory has no parent directory");
        string testsDirectory = Path.Combine(repositoryRoot, "tests");

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];

        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(testsDirectory, file);
            (string code, _, bool balanced) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));

            if (!balanced)
            {
                // Mirrors the sibling guards' handling of the same signal: a file the stripper
                // desynced on cannot be trusted to have surfaced every real assignment it holds,
                // so it is reported rather than silently dropped from coverage.
                offenders.Add(
                    $"{relativePath} <StripCommentsAndStrings desynced on this file: stripped brace depth " +
                    "never returned to zero, so its process-wide-state coverage cannot be trusted>");
                continue;
            }

            offenders.AddRange(
                from mutation in Mutations
                where !string.Equals(relativePath, mutation.OwnerRelativePath, StringComparison.Ordinal)
                      && mutation.Pattern.IsMatch(code)
                select $"{relativePath} -> {mutation.Name}; {mutation.Instead}");
        }

        offenders.Should().BeEmpty(
            "each of these states is shared by every test running in this process at the same moment, so a " +
            "test that swaps one decides what the others see — which is how three separate suite failures in " +
            "one week read as product bugs (see this class's own doc comment). The helper named beside each " +
            "offender scopes the same capture to the calling flow, with nothing to restore and no window for " +
            "another test to write into");

        files.Length.Should().BeGreaterThan(
            100,
            "this is far fewer .cs files than the test tree actually holds — Path.Combine(repositoryRoot, " +
            "\"tests\"), resolved from TestSourceTree.SourceDirectory(), is probably no longer resolving to " +
            "the repository's tests/ directory");

        // A positive control per marker, the counterpart to the file-count floor above: each owning
        // helper does perform the mutation it owns, so a stripped copy of it that matches nothing
        // means this scan has gone dark — a renamed helper, a rewritten pattern, or stripping that
        // has started eating real code — and the offenders assertion is green because it can no
        // longer see an assignment anywhere, not because none exists. The owner only ever exercises
        // the one spelling it happens to use, though, so each marker's own Controls and NotControls
        // carry the rest: every spelling that must be flagged, and the reads beside them that must
        // not be, which is what keeps one alternative of a multi-spelling pattern from going stale
        // unnoticed behind a sibling that still matches.
        foreach (ProcessWideMutation mutation in Mutations)
        {
            string ownerPath = Path.Combine(testsDirectory, mutation.OwnerRelativePath);
            File.Exists(ownerPath).Should().BeTrue(
                $"{mutation.OwnerRelativePath} is the helper this guard sends offenders to; a scan that cannot " +
                "find it is naming a fix that no longer exists");

            (string ownerCode, _, _) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(ownerPath));

            mutation.Pattern.IsMatch(ownerCode).Should().BeTrue(
                $"{mutation.OwnerRelativePath} performs {mutation.Name} on purpose — it is the one place allowed " +
                "to — so this scan must still detect one there; matching nothing means the pattern has gone " +
                "stale against the code and this guard is protecting nothing while reporting success");

            mutation.Controls.Should().NotBeEmpty(
                $"{mutation.Name} needs at least one spelling written out here, or its pattern is only ever " +
                $"proven against whichever one {mutation.OwnerRelativePath} happens to use");

            foreach (string control in mutation.Controls)
            {
                mutation.Pattern.IsMatch(control).Should().BeTrue(
                    $"'{control}' is {mutation.Name} and has to be flagged wherever it appears; this marker's " +
                    "owning helper does not write this particular spelling, so nothing else here would notice " +
                    "the pattern having gone stale against it");
            }

            foreach (string notControl in mutation.NotControls)
            {
                mutation.Pattern.IsMatch(notControl).Should().BeFalse(
                    $"'{notControl}' reads the state or compares it rather than assigning it, which is " +
                    "legitimate anywhere; a pattern that flags it makes this guard noise that every future " +
                    "test author learns to route around instead of a rule they can follow");
            }
        }
    }
}
