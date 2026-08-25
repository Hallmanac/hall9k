using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The strictly-opt-in extra (Decisions Log #31): register start-at-login. Registration
/// only — it never starts the daemon now, exactly as install and start never register it.
/// </summary>
public sealed class DaemonAutostartEnableCommand : Hall9kAsyncCommand<DaemonAutostartEnableCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        IDaemonAutostart autostart = DaemonAutostart.ForCurrentPlatform();
        if (!autostart.IsSupported)
        {
            await Console.Error.WriteLineAsync(autostart.NotSupportedMessage);
            return ExitCodes.Error;
        }

        // Autostart runs whatever the registration points at, so it points at the
        // installed binary — a dev-loop build output would go stale or vanish.
        string installedBinary = Path.Combine(DaemonRuntime.BinDirectory, InstallCommand.BinaryFileName("h9kd"));
        if (!File.Exists(installedBinary))
        {
            await Console.Error.WriteLineAsync(
                $"Autostart points {autostart.MechanismDescription} at the installed binary ({installedBinary}), "
                + "which does not exist yet. Run h9k install first.");
            return ExitCodes.Error;
        }

        // The service manager starts the daemon from its own minimal environment (or, on
        // Windows, from none of it at all — Task Scheduler's Action has no per-job
        // environment of its own the way launchd's plist does), so the registration
        // carries this shell's PATH (and HALL9K_* overrides) into it — otherwise an
        // autostarted daemon runs but cannot find claude or gh.
        IReadOnlyList<KeyValuePair<string, string>> environment = DaemonEnvironment.Capture();
        try
        {
            await autostart.EnableAsync(installedBinary, environment, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await Console.Error.WriteLineAsync(
                $"Autostart enable failed: {autostart.MechanismDescription} was not registered. {exception.Message}");
            return ExitCodes.Error;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Autostart enabled[/]: {autostart.MechanismDescription} registered.");
        AnsiConsole.MarkupLine(
            "[dim]h9kd starts at your next login and restarts after a crash (never after a clean stop). "
            + "Nothing was started now — h9k daemon start does that. Undo with h9k daemon autostart disable.[/]");
        string recorded = Markup.Escape(string.Join(", ", environment.Select(variable => variable.Key)));
        AnsiConsole.MarkupLine(
            $"[dim]Recorded this shell's environment ({recorded}) into the registration: {autostart.MechanismDescription} "
            + "would otherwise start the daemon with a PATH that has no claude, gh, or git on it. Re-run this "
            + "command after moving any of them.[/]");

        IReadOnlyList<string> unresolved = DaemonEnvironment.UnresolvedTools(environment);
        if (unresolved.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Not on the recorded PATH: {Markup.Escape(string.Join(", ", unresolved))}[/] — an "
                + "autostarted daemon would start fine and then fail every run that needs them. Fix the PATH "
                + "(or set HALL9K_CLAUDE_PATH), then re-run h9k daemon autostart enable.");
        }

        return ExitCodes.Ok;
    }
}
