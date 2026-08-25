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
        if (report.ConfigFileMalformed)
        {
            AnsiConsole.MarkupLine(
                "[red]That file exists but is not valid JSON[/] — its settings are being ignored; "
                + "environment variables and built-in defaults still apply. Fix or delete it, then run this again.");
        }

        foreach (string warning in report.UnusableEnvironmentVariables)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{warning}[/]");
        }

        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        foreach ((string label, string value) in OperatingSettingsRendering.Rows(report))
        {
            table.AddRow(label, value.EscapeMarkup());
        }

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
