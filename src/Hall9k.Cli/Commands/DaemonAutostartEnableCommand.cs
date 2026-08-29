using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
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
        IReadOnlyList<string> recordedVariables;
        try
        {
            recordedVariables = await autostart.EnableAsync(installedBinary, environment, cancellationToken);
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
        string recorded = Markup.Escape(string.Join(", ", recordedVariables));
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

        // A mechanism that withholds the connection string (Windows: see
        // WindowsDaemonAutostart) leaves an autostarted h9kd with no way to resolve one
        // unless the platform config file already supplies a working one — the case h9k
        // doctor's start-offer covers, but not the one where an operator configured a
        // reachable Postgres purely by exporting HALL9K_CONNECTION_STRING. A supplied value
        // is no longer proof enough on its own: h9k install now writes its own guessed
        // default into config.json whenever nothing else resolves, without ever starting or
        // confirming a database against it (InstallCommand.WriteDefaultConnectionStringIfUnconfiguredAsync),
        // unlike doctor's start-offer write, which only lands after actually confirming the
        // database came up. So the config file's value is probed for reachability the same
        // way h9k doctor probes one, rather than trusted on presence alone (cycle-4 review).
        bool capturedButNotRecorded = environment.Any(variable => variable.Key == Hall9kDatabase.EnvironmentVariableName)
            && !recordedVariables.Contains(Hall9kDatabase.EnvironmentVariableName);
        if (capturedButNotRecorded)
        {
            // Read state and value together (Hall9kDatabase.ConnectionStringStateAndValueInConfigFile):
            // reading them as two separate calls raced a concurrent h9k config set or editor
            // save, where the second read could disagree with the first and hand a null
            // connection string to DatabaseReachability.ProbeAsync (cycle-6 review).
            (ConfigFileConnectionStringState configFileState, string? configFileConnectionString) =
                Hall9kDatabase.ConnectionStringStateAndValueInConfigFile();
            bool configFileConnectionStringWorks = configFileConnectionString is not null
                && (await DatabaseReachability.ProbeAsync(configFileConnectionString, cancellationToken)).Status
                    == ReachabilityStatus.Reachable;
            if (!configFileConnectionStringWorks)
            {
                string escapedConfigFile = Markup.Escape(Hall9kDatabase.ConfigFile);

                if (configFileState == ConfigFileConnectionStringState.Supplied)
                {
                    // Distinct from the not-configured cases below (Decisions Log #74's
                    // reachable-vs-configured distinction): a value IS configured here, it is
                    // just not answering right now, so the remedy is starting whatever it
                    // names — h9k doctor --yes, when it is Hall9k's own compose Postgres — not
                    // hand-editing a file that already has the value (cycle-6 review: the
                    // previous single fixed tail called this "no connection string configured"
                    // and told the operator to add one that was already present).
                    AnsiConsole.MarkupLine(
                        $"[yellow]{Hall9kDatabase.EnvironmentVariableName} is set in this shell, but {autostart.MechanismDescription} "
                        + $"does not carry it, and {escapedConfigFile} names a connection string that does not "
                        + "currently answer[/] — an autostarted daemon would exit immediately at every logon unless "
                        + "it comes up before then. Run h9k doctor --yes to start it now (if it is Hall9k's own "
                        + $"Postgres), or otherwise fix whatever is keeping {escapedConfigFile}'s connection string "
                        + "from answering.");
                }
                else
                {
                    // h9k doctor is not the fix for these three: its start-offer only ever
                    // writes a connection string on its own not-configured path, and this
                    // warning fires precisely when one already resolves (from the environment
                    // variable), so doctor would report healthy and touch config.json not at
                    // all — the real fix is the same hand-edit doctor's own not-configured
                    // message points at.
                    string configFileProblem = configFileState switch
                    {
                        ConfigFileConnectionStringState.Missing =>
                            $"{escapedConfigFile} does not exist to supply one another way",
                        ConfigFileConnectionStringState.PresentWithoutConnectionString =>
                            $"{escapedConfigFile} exists but does not carry a connectionString",
                        ConfigFileConnectionStringState.Malformed =>
                            $"{escapedConfigFile} exists but is not valid JSON",
                        _ => throw new NotSupportedException($"Unexpected {nameof(ConfigFileConnectionStringState)}: {configFileState}"),
                    };

                    AnsiConsole.MarkupLine(
                        $"[yellow]{Hall9kDatabase.EnvironmentVariableName} is set in this shell, but {autostart.MechanismDescription} "
                        + $"does not carry it, and {configFileProblem}[/] — an autostarted daemon would exit immediately "
                        + $"at every logon with no connection string configured. Add it to {escapedConfigFile} by hand "
                        + "(write {\"connectionString\": \"…\"} there, fixing its JSON first if that is what is broken) "
                        + "to give it a durable one.");
                }
            }
        }

        return ExitCodes.Ok;
    }
}
