using System.ComponentModel;
using System.Diagnostics;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The macOS signal fence around every agent session <see cref="ClaudeExecutor"/> spawns: a
/// seatbelt profile that lets the session signal only processes it started. Origin incident
/// (2026-10-09): a dispatched session ran <c>pkill -f "vite" -P 1</c>, BSD pkill stops option
/// parsing at the first pattern, so <c>-P</c> and <c>1</c> became patterns and about 64 of the
/// operator's own processes received SIGTERM. A stopgap, by Brian's ruling of 2026-10-10, until
/// Claude Code's own sandbox can run a headless browser.
/// <para>
/// The platform's own process managers stay policy-free: <see cref="ClaudeExecutor"/> composes
/// <see cref="Compose"/> ahead of the claude command it hands to <c>IProcessManager.Spawn</c>.
/// The wrapper is an <c>exec</c> chain (<c>sh -c "exec sandbox-exec ..."</c>, and sandbox-exec
/// then execs claude), so the pid the daemon records is still the agent's own for its whole life.
/// </para>
/// </summary>
public sealed class SessionContainment(bool isMacOs, Func<string?> probe)
{
    /// <summary>
    /// The one seatbelt profile every contained session runs under. Reads: everything is allowed
    /// except signals, and a signal is allowed only to a process in the same sandbox (the session
    /// and whatever it starts). <c>/bin/ps</c> is the single exec that escapes the profile, because
    /// Claude Code's own Bash-tool tree kill uses <c>ps -A</c> and a setuid binary cannot run under
    /// any sandbox-exec wrapper at all; <c>ps</c> only reads, so running it outside costs nothing.
    /// <para>
    /// Boundary. It covers any signal from the session or anything it starts, including detached
    /// processes (<c>( sleep 60 &amp; )</c>) and ones that moved to their own process group or session:
    /// the profile follows the sandbox, not the process tree. It does not cover Docker, launchctl,
    /// Apple Events, file writes, sessions started by <c>h9k task start</c> or <c>h9k task delegate</c>
    /// (those launch through <c>HeadlessLaunch</c>, which is not wrapped), or Linux and Windows,
    /// where no wrapper is applied.
    /// </para>
    /// <para>
    /// Known incompatibilities. Anything inside a session that applies its own seatbelt sandbox
    /// fails with "Operation not permitted", because a sandbox cannot be nested: Chrome with its
    /// sandbox on, Claude Code's own sandbox setting, and <c>swift build</c> without
    /// <c>--disable-sandbox</c>. Playwright launches Chromium with <c>--no-sandbox</c> by default
    /// (its <c>BrowserType.launch</c> option <c>chromiumSandbox</c> is documented as defaulting to
    /// false), so it is unaffected until someone turns that on. Setuid binaries other than
    /// <c>/bin/ps</c> (top, sudo, su, login, at, crontab, traceroute) cannot run. A signal-0 liveness
    /// probe (<c>kill -0</c>) of a process outside the sandbox gets "Operation not permitted", so a
    /// session cannot tell whether an outside process is alive.
    /// </para>
    /// <para>
    /// Why this and not the vendor's sandbox. Claude Code's built-in sandbox, Codex, and srt all
    /// call this same <c>sandbox-exec</c>, so its deprecation risk is shared by every option, not
    /// avoided by switching. The vendor sandbox was set aside because headless Chromium cannot start
    /// under it on macOS (Claude Code issue 82660). The planned exit is to move to it, triggered when
    /// Chromium launches under it with <c>--no-sandbox</c>.
    /// </para>
    /// </summary>
    public const string Profile =
        "(version 1)(allow default)(deny signal)(allow signal (target same-sandbox))"
        + "(allow process-exec (literal \"/bin/ps\") (with no-sandbox))";

    public const string SandboxExecPath = "/usr/bin/sandbox-exec";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The wrapper as the production daemon uses it: macOS only, verified against the real sandbox-exec.</summary>
    public static SessionContainment ForCurrentPlatform() => new(OperatingSystem.IsMacOS(), ProbeSandboxExec);

    /// <summary>A containment that never wraps, for callers that spawn nothing real.</summary>
    public static SessionContainment Off { get; } = new(false, static () => null);

    /// <summary>
    /// <paramref name="command"/> prefixed so the shell execs it under <see cref="Profile"/>. The
    /// profile is single-quoted for <c>/bin/sh</c>; it holds no single quote of its own.
    /// </summary>
    public static string Compose(string command) => $"{SandboxExecPath} -p '{Profile}' {command}";

    /// <summary>
    /// Returns <paramref name="command"/> unchanged unless containment is on and this is macOS, in
    /// which case it is verified and wrapped. Linux and Windows never reach the probe.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Containment applies but sandbox-exec is missing or does not apply the profile: no agent
    /// process may start, and the message is what the run's launch failure records.
    /// </exception>
    public string Apply(string command, bool enabled)
    {
        if (!enabled || !isMacOs)
        {
            return command;
        }

        if (probe() is { } reason)
        {
            throw new InvalidOperationException(
                "Session containment could not be applied, so no agent process was started: "
                + $"{reason} Containment keeps a dispatched session from signalling processes it did not "
                + "start (SessionContainment.Profile); h9k config set --session-containment false turns it "
                + "off at the daemon's next start.");
        }

        return Compose(command);
    }

    /// <summary>
    /// Confirms <see cref="SandboxExecPath"/> exists and applies <see cref="Profile"/> over
    /// <c>/usr/bin/true</c>. sandbox-exec reports a bad profile (exit 65) or a failed apply (exit 71)
    /// only after the spawn has already returned a pid, so this has to run before it. Null on success.
    /// </summary>
    internal static string? ProbeSandboxExec()
    {
        if (!File.Exists(SandboxExecPath))
        {
            return $"{SandboxExecPath} does not exist.";
        }

        try
        {
            using Process probe = new()
            {
                StartInfo = new ProcessStartInfo(SandboxExecPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            probe.StartInfo.ArgumentList.Add("-p");
            probe.StartInfo.ArgumentList.Add(Profile);
            probe.StartInfo.ArgumentList.Add("/usr/bin/true");
            probe.Start();
            Task<string> standardError = probe.StandardError.ReadToEndAsync();
            _ = probe.StandardOutput.ReadToEndAsync();
            if (!probe.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                probe.Kill();
                return $"{SandboxExecPath} did not finish applying the profile within {ProbeTimeout.TotalSeconds:0} seconds.";
            }

            return probe.ExitCode == 0
                ? null
                : $"{SandboxExecPath} exited {probe.ExitCode} applying the profile: {standardError.GetAwaiter().GetResult().Trim()}";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return $"{SandboxExecPath} could not be run: {exception.Message}";
        }
    }
}
