namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The platform-imposed Claude Code settings (PLAN.md §6.6, and the 2026-09-01 timeout
/// finding below): agents never author co-authored-by trailers, and a session's command tool
/// gets timeout headroom sized for this platform's own gates rather than Claude Code's stock
/// defaults. Shared between the daemon's headless spawn
/// (<c>Hall9k.Daemon.Execution.ClaudeExecutor</c>) and an operator's interactive claim
/// (<c>h9k task work</c>) so both settings reach a session identically regardless of who
/// launched it — the CLI cannot reference <c>Hall9k.Daemon</c>, so this is the shared home
/// both sides read the content from, the same pattern <see cref="WorkPromptBuilder"/> already
/// uses for the prompt itself.
/// <para>
/// <b>The command timeout, and why (2026-09-01 finding, 399 fix-session transcripts mined):</b>
/// Claude Code's Bash tool defaults a command's timeout to <c>BASH_DEFAULT_TIMEOUT_MS</c> (stock
/// 120000ms = 2 minutes) and caps any explicit per-command timeout a session requests at
/// <c>BASH_MAX_TIMEOUT_MS</c> (stock 600000ms = 10 minutes, verified empirically against the
/// installed Claude Code 2.1.258 binary rather than assumed from memory — its bundled CLI
/// resolves exactly those two defaults). The stock 2-minute default killed obedient dispatched
/// sessions running this platform's own 8-minute-and-growing foreground <c>dotnet test</c> gate;
/// sessions adapted by detaching the suite into the background and then dying waiting on a
/// result that was never coming. The stock 10-minute cap made foreground compliance impossible
/// outright on the days the (at the time unbounded-parallelism) suite ran longer than that. Both
/// env vars are read by every Claude Code session — there is no dedicated settings.json field for
/// either, so this is the mechanism, set here through the generic <c>env</c> passthrough
/// documented in the settings schema.
/// </para>
/// <para>
/// <b>Sizing (2026-09-02 finding: a compile-time constant went stale the moment an operator
/// raised the live option it claimed to mirror):</b> <see cref="Build"/> takes the command
/// timeout to ship as <c>BASH_DEFAULT_TIMEOUT_MS</c>, so a caller with a live-configured ceiling
/// in reach — <c>ClaudeExecutor</c>, which resolves <c>IOptions&lt;Hall9k.Daemon.DaemonOptions&gt;
/// .Value.VerifyGateTimeout</c> exactly as <c>VerificationRunner</c> already does — hands that
/// value straight through, and a foreground gate run is sized for whatever ceiling the daemon
/// itself is actually enforcing, not a number frozen at build time. <c>BASH_MAX_TIMEOUT_MS</c> is
/// always double whatever default is requested, so a session can still ask for more than the
/// default via its own explicit per-command timeout on a day the suite runs long — the platform
/// sizes the floor; the ceiling stays a session's own call. <see cref="DefaultCommandTimeout"/>
/// (30 minutes, mirroring <c>DaemonOptions.VerifyGateTimeout</c>'s own default) is what a caller
/// with no live option in reach falls back to — today, only <c>h9k task work</c>
/// (<c>TaskWorkCommand</c>, in <c>Hall9k.Cli</c>), which structurally cannot reference
/// <c>Hall9k.Daemon</c> at all (Reference graph: Cli -> Domain + Connectors). That the platform's
/// 30 minutes is written down more than once is a choice about which project owns the number
/// rather than a reference the compiler forbids — see <see cref="DefaultCommandTimeout"/> for
/// which direction is open and why it is not taken. <c>TaskVerifyCommand</c>'s own hardcoded
/// 30-minute gate timeout is a third copy of the same number, pinned to nothing.
/// An operator who raises <c>VerifyGateTimeout</c> past 30 minutes therefore gets the raised
/// ceiling on every headless dispatch. Two CLI surfaces stay unmoved by that setting, because
/// neither can reach <c>DaemonOptions</c>: a foreground gate run inside an interactive
/// <c>h9k task work</c> claim still falls back to the 30-minute <see cref="DefaultCommandTimeout"/>
/// above, and <c>h9k task verify</c>'s own gate timeout (<c>TaskVerifyCommand</c>, a separate
/// hardcoded 30-minute <c>CancelAfter</c> on the gate process itself, not a
/// <see cref="Build"/> caller at all) stays pinned regardless of the option.
/// </para>
/// </summary>
public static class ClaudeSettingsFile
{
    /// <summary>
    /// Mirrors <c>Hall9k.Daemon.DaemonOptions.VerifyGateTimeout</c>'s own default, pinned to it by
    /// <c>ClaudeSettingsFileTests</c>. The duplication is deliberate rather than forced
    /// (2026-09-02 adversarial review, which caught this comment claiming otherwise): this project
    /// cannot reference <c>Hall9k.Daemon</c>, but <c>Hall9k.Daemon</c> does reference this one, so
    /// <c>VerifyGateTimeout</c>'s initializer could name this constant and collapse the two into
    /// one. It deliberately does not, because that direction leaves the daemon's own gate ceiling
    /// defined by a Claude Code settings type: the gate timeout is the number that means
    /// something on its own, and this constant is the mirror of it, not the reverse. A test holds
    /// the mirror true instead. This is the fallback for a caller with no live-configured ceiling
    /// in reach.
    /// </summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Builds the settings-file body a session should launch with, sizing
    /// <c>BASH_DEFAULT_TIMEOUT_MS</c> to <paramref name="commandTimeout"/> and
    /// <c>BASH_MAX_TIMEOUT_MS</c> to double that.
    /// </summary>
    public static string Build(TimeSpan commandTimeout)
    {
        long defaultMilliseconds = (long)commandTimeout.TotalMilliseconds;
        long maxMilliseconds = defaultMilliseconds * 2;
        return $$$"""{"includeCoAuthoredBy": false, "env": {"BASH_DEFAULT_TIMEOUT_MS": "{{{defaultMilliseconds}}}", "BASH_MAX_TIMEOUT_MS": "{{{maxMilliseconds}}}"}}""";
    }
}
