using System.Globalization;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// A verification gate's command, described as steps rather than as shell text.
/// <c>VerificationRunner</c> hands a project's gate command to <c>/bin/sh -c</c> on Unix and to
/// <c>cmd.exe /c</c> on Windows — that is production's own choice, not this helper's — so a test
/// gate written in one dialect is a test that only ever ran on one platform. Every shape these
/// tests need (run a portable command, print, print into a file, create a marker, pause, branch
/// on a marker's existence, exit with a code) is spelled here once per dialect.
/// <para>
/// Windows notes worth knowing before adding a step: the whole command already runs inside
/// <c>VerificationRunner</c>'s own <c>(…) &gt; log 2&gt;&amp;1</c> block, so any parenthesis in
/// echoed text has to be caret-escaped or it closes that block early; <c>cmd.exe</c> has no
/// sub-second wait, hence the <c>powershell</c> hop in <see cref="Pause"/>; and
/// <c>echo x &amp;&amp; …</c> emits the space before the <c>&amp;&amp;</c> as part of the line,
/// which is why assertions on gate output are substring checks.
/// </para>
/// </summary>
internal sealed class GateScript
{
    private readonly List<Step> _steps = [];

    public static GateScript New() => new();

    /// <summary>
    /// The gate that does nothing and succeeds — the old <c>true</c>, which cmd.exe has no
    /// command for.
    /// </summary>
    public static string Passes => New().Exit(0).Command;

    /// <summary>
    /// Runs a command that already means the same thing in both shells — <c>dotnet test</c> and
    /// friends — so a gate can chain one with steps that do not.
    /// </summary>
    public GateScript Run(string command)
    {
        _steps.Add(new Step.Run(command));
        return this;
    }

    /// <summary>Writes one line to the gate's own log (its redirected standard output).</summary>
    public GateScript Print(string text)
    {
        _steps.Add(new Step.Print(text));
        return this;
    }

    /// <summary>
    /// Writes the same line <paramref name="times"/> times — padding, for a test that needs an
    /// earlier marker pushed out of the tail the run records.
    /// </summary>
    public GateScript PrintRepeated(string text, int times)
    {
        _steps.Add(new Step.PrintRepeated(text, times));
        return this;
    }

    /// <summary>
    /// Writes one line into <paramref name="path"/> instead of the log — the channel a gate that
    /// died while queued on the container gate leaves its evidence on. Use
    /// <see cref="EnvironmentPath"/> to name a path the spawn passes in as an environment
    /// variable.
    /// </summary>
    public GateScript PrintTo(string text, string path)
    {
        _steps.Add(new Step.PrintTo(text, path));
        return this;
    }

    /// <summary>Creates an empty file, the way a gate leaves a marker for its own retry to find.</summary>
    public GateScript CreateFile(string path)
    {
        _steps.Add(new Step.CreateFile(path));
        return this;
    }

    /// <summary>Blocks, for a gate that has to outlive the runner's own timeout.</summary>
    public GateScript Pause(TimeSpan duration)
    {
        _steps.Add(new Step.Pause(duration));
        return this;
    }

    /// <summary>
    /// Ends the gate with the given exit code, which is what pass and fail actually mean here.
    /// </summary>
    public GateScript Exit(int code)
    {
        _steps.Add(new Step.Exit(code));
        return this;
    }

    /// <summary>Appends one line to <paramref name="path"/>, leaving whatever is already there.</summary>
    public GateScript AppendTo(string text, string path)
    {
        _steps.Add(new Step.AppendTo(text, path));
        return this;
    }

    /// <summary>
    /// Runs one of two scripts depending on whether the file at <paramref name="path"/> exists —
    /// the shape every flaky-gate test uses to make its first attempt differ from its retry.
    /// </summary>
    public GateScript BranchOnFile(string path, GateScript whenPresent, GateScript whenMissing)
    {
        _steps.Add(new Step.BranchOnFile(path, whenPresent, whenMissing));
        return this;
    }

    /// <summary>
    /// The same, on a directory — how a gate tells the clean-base checkout it is being compared
    /// against (a real repository, so it has a <c>.git</c> directory) from the run's own plain
    /// non-git worktree.
    /// </summary>
    public GateScript BranchOnDirectory(string path, GateScript whenPresent, GateScript whenMissing)
    {
        _steps.Add(new Step.BranchOnDirectory(path, whenPresent, whenMissing));
        return this;
    }

    /// <summary>
    /// A path under a directory the gate spawn names in an environment variable, spelled the way
    /// this platform's shell expands one.
    /// </summary>
    public static string EnvironmentPath(string variableName, string fileName) =>
        PlatformShell.IsPosix ? $"${variableName}/{fileName}" : $"%{variableName}%\\{fileName}";

    /// <summary>A gate whose whole job is to echo one environment variable's value.</summary>
    public static string PrintEnvironmentVariable(string variableName) =>
        PlatformShell.IsPosix ? $"echo ${variableName}" : $"echo %{variableName}%";

    /// <summary>
    /// The rendered command, in this platform's own shell dialect. Steps are sequenced
    /// unconditionally — <c>;</c> on Unix, <c>&amp;</c> on Windows — rather than with
    /// <c>&amp;&amp;</c>: cmd.exe treats an <c>if</c> whose condition was false as a command that
    /// never ran, so <c>&amp;&amp;</c> after one silently swallows the whole rest of the gate.
    /// Nothing here needs short-circuiting anyway; a step that fails is always the last one.
    /// </summary>
    public string Command => string.Join(PlatformShell.IsPosix ? "; " : " & ", _steps.Select(Render));

    private static string Render(Step step) => PlatformShell.IsPosix ? RenderPosix(step) : RenderCmd(step);

    private static string RenderPosix(Step step) => step switch
    {
        Step.Run run => run.Command,
        Step.Print print => $"echo {PlatformShell.PosixLiteral(print.Text)}",
        Step.PrintRepeated repeated =>
            $"for i in $(seq 1 {repeated.Times}); do echo {PlatformShell.PosixLiteral(repeated.Text)}; done",
        Step.PrintTo printTo => $"echo {PlatformShell.PosixLiteral(printTo.Text)} > \"{printTo.Path}\"",
        Step.AppendTo appendTo => $"echo {PlatformShell.PosixLiteral(appendTo.Text)} >> \"{appendTo.Path}\"",
        Step.CreateFile create => $"touch \"{create.Path}\"",
        Step.Pause pause =>
            $"sleep {pause.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}",
        Step.Exit exit => $"exit {exit.Code}",
        Step.BranchOnFile branch => RenderPosixBranch("-f", branch.Path, branch.WhenPresent, branch.WhenMissing),
        Step.BranchOnDirectory branch => RenderPosixBranch("-d", branch.Path, branch.WhenPresent, branch.WhenMissing),
        _ => throw new InvalidOperationException($"unhandled step '{step}'"),
    };

    private static string RenderPosixBranch(
        string test, string path, GateScript whenPresent, GateScript whenMissing) =>
        $"if test {test} \"{path}\"; then {whenPresent.Command}; else {whenMissing.Command}; fi";

    private static string RenderCmd(Step step) => step switch
    {
        Step.Run run => run.Command,
        Step.Print print => $"echo {CmdText(print.Text)}",
        Step.PrintRepeated repeated =>
            $"(for /l %i in (1,1,{repeated.Times}) do @echo {CmdText(repeated.Text)})",
        Step.PrintTo printTo => $"(echo {CmdText(printTo.Text)}) > \"{printTo.Path}\"",
        Step.AppendTo appendTo => $"(echo {CmdText(appendTo.Text)}) >> \"{appendTo.Path}\"",
        Step.CreateFile create => $"type nul > \"{create.Path}\"",
        // cmd.exe has no wait of its own that survives a redirected console, so the one hop out
        // to PowerShell is the honest option; its own start-up cost is a fraction of a second
        // against pauses measured in tens of them.
        Step.Pause pause =>
            $"powershell -NoProfile -Command Start-Sleep -Milliseconds {(int)pause.Duration.TotalMilliseconds}",
        Step.Exit exit => $"exit {exit.Code}",
        Step.BranchOnFile branch => RenderCmdBranch(branch.Path, branch.WhenPresent, branch.WhenMissing),
        // The trailing separator is what makes `if exist` a directory test rather than a
        // name test — cmd.exe has no `-d` of its own.
        Step.BranchOnDirectory branch =>
            RenderCmdBranch($"{branch.Path}\\", branch.WhenPresent, branch.WhenMissing),
        _ => throw new InvalidOperationException($"unhandled step '{step}'"),
    };

    /// <summary>
    /// The whole <c>if</c> is parenthesized, which looks redundant and is not: when an arm
    /// contains a redirection, cmd.exe silently discards everything sequenced after a bare
    /// <c>if</c> — the counter gate in <c>VerificationRunnerTests</c> ran its comparison, wrote
    /// its evidence file, and then never echoed or exited at all. Wrapping the statement scopes
    /// the redirection so the steps after it run.
    /// </summary>
    private static string RenderCmdBranch(string path, GateScript whenPresent, GateScript whenMissing) =>
        $"(if exist \"{path}\" ({whenPresent.Command}) else ({whenMissing.Command}))";

    /// <summary>
    /// Escapes the characters cmd.exe's parser acts on before <c>echo</c> ever sees them.
    /// <c>^</c> goes first, or it would escape the carets added after it. <c>%</c> is not on the
    /// list and cannot be: on a command line (as opposed to inside a batch file) there is no
    /// escape for it, so a step whose text needs a literal percent sign needs a different
    /// mechanism than <c>echo</c>.
    /// </summary>
    private static string CmdText(string text)
    {
        string escaped = text.Replace("^", "^^");
        foreach (char special in "&<>|()")
        {
            escaped = escaped.Replace(special.ToString(), $"^{special}");
        }

        return escaped;
    }

    private abstract record Step
    {
        public sealed record Run(string Command) : Step;

        public sealed record Print(string Text) : Step;

        public sealed record PrintRepeated(string Text, int Times) : Step;

        public sealed record PrintTo(string Text, string Path) : Step;

        public sealed record AppendTo(string Text, string Path) : Step;

        public sealed record CreateFile(string Path) : Step;

        public sealed record Pause(TimeSpan Duration) : Step;

        public sealed record Exit(int Code) : Step;

        public sealed record BranchOnFile(string Path, GateScript WhenPresent, GateScript WhenMissing) : Step;

        public sealed record BranchOnDirectory(string Path, GateScript WhenPresent, GateScript WhenMissing) : Step;
    }
}
