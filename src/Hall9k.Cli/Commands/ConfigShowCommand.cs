using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The daemon's operating settings as they are effectively resolved right now — the same
/// precedence <c>DaemonOptions</c> binds by at daemon startup (env, then the platform config
/// file, then the built-in default) — with where each one actually came from named beside it
/// (backlog 59).
/// </summary>
public sealed class ConfigShowCommand : Hall9kAsyncCommand<ConfigShowCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cancellationToken);

        if (File.Exists(Hall9kDatabase.ConfigFile))
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]config file: {Hall9kDatabase.ConfigFile}[/]");
        }
        else if (AnyFromEnvironment(report))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]config file: {Hall9kDatabase.ConfigFile} (not created yet; h9k config set creates it)[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]config file: {Hall9kDatabase.ConfigFile} (not created yet — every setting below is a built-in default; h9k config set creates it)[/]");
        }
        foreach (string line in OperatingSettingsRendering.ProblemLines(report))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{line}[/]");
        }

        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        foreach ((string label, string value) in OperatingSettingsRendering.Rows(report))
        {
            table.AddRow(label, value.EscapeMarkup());
        }

        // Not part of the report above: nothing binds this through DaemonOptions (there is no
        // daemon-side reclaim to configure, ever — h9k status reads it fresh on every render), so
        // it carries no environment-variable tier and none of that pipeline's crash consequences.
        // Read through the same non-throwing TryReadOperatingSettingsAsync the report above
        // already used (report.ConfigFileProblem already names a malformed file), rather than the
        // write path's throwing ReadOperatingSettingsAsync: this is the one command built to
        // diagnose a broken config file, so it must still print its table on one — not abort
        // after the red problem line with the table never written (independent pre-PR review,
        // cycle 1).
        OperatingSettings configured = (await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken)).Settings;
        int staleAfterDays = configured.InteractiveClaimStaleAfterDays ?? OperatingSettings.DefaultInteractiveClaimStaleAfterDays;
        table.AddRow(
            "interactive-claim-stale-after-days",
            $"{staleAfterDays} ({(configured.InteractiveClaimStaleAfterDays is null ? "default" : "config file")})".EscapeMarkup());

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "\n[dim]Change a setting:[/] h9k config set --max-concurrent-agent-sessions 4");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Whether any setting in <paramref name="report"/> resolved from an environment variable —
    /// the "not created yet" header must not claim every setting below is a built-in default
    /// when one of them is actually an env override outranking that default.
    /// </summary>
    private static bool AnyFromEnvironment(OperatingSettingsReport report) =>
        report.MaxConcurrentAgentSessions.Origin == SettingOrigin.EnvironmentVariable
        || report.DefaultModel.Origin == SettingOrigin.EnvironmentVariable
        || report.ModelByRole.Any(role => role.Model.Origin == SettingOrigin.EnvironmentVariable);
}
