using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
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
        // The raw configured value is still shown — this is the one command built to diagnose a
        // hand-edited file — but AttentionComposer only ever nudges at the clamped one, so an
        // out-of-range value (ConfigSetCommand.Validate guards only the CLI write path) says both
        // rather than reporting a number the board never actually applies (conformance review,
        // cycle 2).
        int clampedStaleAfterDays = Math.Clamp(staleAfterDays, 1, AttentionComposer.MaxInteractiveClaimStaleAfterDays);
        string staleAfterDaysOrigin = configured.InteractiveClaimStaleAfterDays is null
            ? "default"
            : staleAfterDays == clampedStaleAfterDays
                ? "config file"
                : $"config file; out of the board's 1-{AttentionComposer.MaxInteractiveClaimStaleAfterDays} day range, so it nudges at {clampedStaleAfterDays}";
        table.AddRow(
            "interactive-claim-stale-after-days",
            $"{staleAfterDays} ({staleAfterDaysOrigin})".EscapeMarkup());

        AnsiConsole.Write(table);

        foreach (string line in await SpendLinesAsync(report, cancellationToken))
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]{line}[/]");
        }

        AnsiConsole.MarkupLine(
            "\n[dim]Change a setting:[/] h9k config set --max-concurrent-task-runs 2");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The current period's recorded spend, by model, shown whether or not a budget is set
    /// (observability precedes enforcement, backlog: spend-governor step three) — so the
    /// calibration loop (run, observe a week's real burn, set the budget under it, adjust) starts
    /// the day this merges rather than the day a number is chosen. Summed live from the event
    /// store rather than read through <see cref="OperatingSettingsResolver"/>'s own DB-free walk,
    /// so this is the one row on this screen that needs a database — degraded gracefully rather
    /// than taking the rest of this command's DB-free config diagnosis down with it, since
    /// h9k config show is also how a fresh install with no database configured yet inspects what
    /// it is about to run with.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SpendLinesAsync(
        OperatingSettingsReport report, CancellationToken cancellationToken)
    {
        try
        {
            using DocumentStore store = CliStore.Open();
            await using IQuerySession session = store.QuerySession();
            SpendPressure spend = await SpendPressure.ReadAsync(session, report, DateTimeOffset.UtcNow, cancellationToken);
            return
            [
                spend.SummaryLine,
                .. spend.ByModel.Select(entry =>
                    $"  {(entry.Model == AgentModel.Unknown ? "(unknown model)" : entry.Model.Value)}: {entry.TotalInputTokens:N0} tokens"),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [$"spend this period: unavailable ({exception.Message})"];
        }
    }

    /// <summary>
    /// Whether any setting in <paramref name="report"/> resolved from an environment variable —
    /// the "not created yet" header must not claim every setting below is a built-in default
    /// when one of them is actually an env override outranking that default.
    /// </summary>
    private static bool AnyFromEnvironment(OperatingSettingsReport report) =>
        report.MaxConcurrentAgentSessions.Origin == SettingOrigin.EnvironmentVariable
        || report.MaxConcurrentTaskRuns.Origin == SettingOrigin.EnvironmentVariable
        || report.SessionCapPerRun.Origin == SettingOrigin.EnvironmentVariable
        || report.DefaultModel.Origin == SettingOrigin.EnvironmentVariable
        || report.ModelByRole.Any(role => role.Model.Origin == SettingOrigin.EnvironmentVariable)
        || report.MaxComplianceReviewCycles.Origin == SettingOrigin.EnvironmentVariable
        || report.MaxAdversarialReviewCycles.Origin == SettingOrigin.EnvironmentVariable
        || report.MaxFinalFullPassRounds.Origin == SettingOrigin.EnvironmentVariable
        || report.LifetimeReviewCycleBudget.Origin == SettingOrigin.EnvironmentVariable
        || report.SpendBudgetTokens.Origin == SettingOrigin.EnvironmentVariable
        || report.SpendPeriod.Origin == SettingOrigin.EnvironmentVariable;
}
