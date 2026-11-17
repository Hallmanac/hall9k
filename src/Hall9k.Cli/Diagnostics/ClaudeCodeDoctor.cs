using System.Text.Json;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>What one bound connection's check came to, with the status it read when it read one.</summary>
internal sealed record ClaudeCodeDoctorProbe(ClaudeAccountCheckResult? Result, string? Failure)
{
    public bool Passed => Result is { Passed: true };
}

/// <summary>
/// <c>h9k doctor</c>'s section for claude-code connections, shaped like <see cref="LinearDoctor"/>: only worth
/// running once this node records one. It checks each connection a project is bound to the way a spawn would,
/// printing the account it found or the problem and its fix; warns about a credential that outranks a
/// directory's login, in this shell's environment or in a bound directory's <c>settings.json</c>; and warns
/// when a solution's members run under different accounts. No failure here stops the rest of the doctor run.
/// </summary>
internal static class ClaudeCodeDoctor
{
    /// <summary>The environment variables that outrank a config directory's own login when Claude Code runs.</summary>
    internal static readonly IReadOnlyList<string> OverrideVariables =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"];

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Claude Code accounts: could not be checked ({exception.Message})[/]");
        }
    }

    private static async Task WriteAsync(CancellationToken cancellationToken)
    {
        using DocumentStore store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ConnectionDetails> connections = await ClaudeCodeAccounts.ListAsync(session, cancellationToken);
        if (connections.Count == 0)
        {
            return;
        }

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        AnsiConsole.MarkupLine("[bold]Claude Code accounts[/]");

        foreach (string variable in EnvironmentOverrides(Environment.GetEnvironmentVariable))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  {variable} is set in this shell[/], and it outranks every config directory's own login, so a "
                + "bound account's check reports it instead of the account and refuses the session. Unset it in the "
                + "environment h9k and h9kd run in.");
        }

        foreach (ConnectionDetails connection in connections)
        {
            string[] bound = [.. projects.Where(project => project.ClaudeCodeConnectionId == connection.Id).Select(project => project.Name).Order()];
            if (bound.Length == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]  {DomainId.Short(connection.Id)} {connection.ExternalAccountId.EscapeMarkup()}: no project is "
                    + "bound to it, so it is not checked.[/]");
                continue;
            }

            ClaudeCodeAccount account = ClaudeCodeAccount.For(connection);
            ClaudeCodeDoctorProbe probe = await ProbeAsync(
                () => CliClaudeAccount.Checker.CheckAsync(account, reusePass: false, cancellationToken).AsTask(),
                cancellationToken);
            AnsiConsole.MarkupLine(ProbeLine(connection, bound, probe));

            foreach (string setting in await SettingsOverridesAsync(connection.ExternalAccountId, cancellationToken))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]  {connection.ExternalAccountId.EscapeMarkup()}'s settings.json sets {setting.EscapeMarkup()}[/], "
                    + "which outranks the directory's own login for every session started under it. Remove it so "
                    + "the check can confirm the account.");
            }
        }

        foreach (string warning in SolutionMismatches(projects))
        {
            AnsiConsole.MarkupLine(warning);
        }
    }

    /// <summary>
    /// The check itself, apart from the printing and the store so it can be driven with a fake probe. Never
    /// throws: a probe that fails in any way is reported as a problem, so one bad directory cannot abort the
    /// doctor run.
    /// </summary>
    internal static async Task<ClaudeCodeDoctorProbe> ProbeAsync(
        Func<Task<ClaudeAccountCheckResult>> check, CancellationToken cancellationToken)
    {
        try
        {
            return new ClaudeCodeDoctorProbe(await check(), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ClaudeCodeDoctorProbe(null, RelayedText.OneLine(exception.Message));
        }
    }

    internal static string ProbeLine(ConnectionDetails connection, IReadOnlyList<string> boundProjects, ClaudeCodeDoctorProbe probe)
    {
        string projects = string.Join(", ", boundProjects).EscapeMarkup();
        string label = $"{DomainId.Short(connection.Id)} {connection.ExternalAccountId.EscapeMarkup()}";
        return probe switch
        {
            { Passed: true, Result.Status: { } status } =>
                $"[green]  {label}[/] is logged in as {ExternalText.OneLineMarkup(status.Email ?? string.Empty)}, "
                + $"{ExternalText.OneLineMarkup(status.OrganizationName ?? status.OrganizationId ?? string.Empty)}"
                + (status.SubscriptionType is { } subscription ? $" ({ExternalText.OneLineMarkup(subscription)})" : string.Empty)
                + $" [dim]({projects} run under it)[/]",
            { Result: { } refused } =>
                $"[red]  {label}[/] fails its check, so {projects} start no Claude Code session: "
                + ExternalText.OneLineMarkup(refused.Message),
            _ =>
                $"[yellow]  {label}[/] could not be checked ({ExternalText.OneLineMarkup(probe.Failure ?? "no reason given")}), "
                + $"so it is neither known to work nor known to be refused; {projects} run under it.",
        };
    }

    /// <summary>The override variables set in an environment, in the order they are listed.</summary>
    internal static IReadOnlyList<string> EnvironmentOverrides(Func<string, string?> read) =>
        [.. OverrideVariables.Where(variable => !string.IsNullOrEmpty(read(variable)))];

    /// <summary>
    /// What a directory's <c>settings.json</c> sets that outranks the directory's own login: an
    /// <c>apiKeyHelper</c>, or an override variable in its <c>env</c> block. Unreadable or malformed JSON
    /// reports nothing, since Claude Code would not read it either.
    /// </summary>
    internal static IReadOnlyList<string> SettingsOverrides(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            List<string> found = [];
            if (document.RootElement.TryGetProperty("apiKeyHelper", out _))
            {
                found.Add("apiKeyHelper");
            }

            if (document.RootElement.TryGetProperty("env", out JsonElement env) && env.ValueKind == JsonValueKind.Object)
            {
                found.AddRange(OverrideVariables.Where(variable => env.TryGetProperty(variable, out _)).Select(variable => $"env.{variable}"));
            }

            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<string>> SettingsOverridesAsync(string directory, CancellationToken cancellationToken)
    {
        string path = Path.Join(directory, "settings.json");
        try
        {
            return File.Exists(path)
                ? SettingsOverrides(await File.ReadAllTextAsync(path, cancellationToken))
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// One warning per open solution whose members on this node run under different accounts. A solution has
    /// no binding of its own and follows its members (<see cref="ClaudeCodeAccountBinding.ForSolution"/>), so a
    /// split solution has no one account to resolve to (the resolver gives the default login), and its members'
    /// sessions bill different accounts, which is rarely what was meant.
    /// </summary>
    internal static IReadOnlyList<string> SolutionMismatches(IEnumerable<ProjectDetails> projects) =>
        [.. projects
            .Where(project => project.Solution is { Holds: true })
            .GroupBy(project => project.Solution?.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(members => members.Select(member => member.ClaudeCodeConnectionId).Distinct().Count() > 1
                && ClaudeCodeAccountBinding.ForSolution(members.Select(member => member.ClaudeCodeConnectionId)) is null)
            .Select(members =>
                $"[yellow]  Solution {(members.First().Solution?.Name ?? members.Key).EscapeMarkup()}'s members run under "
                + "different Claude Code accounts[/] ("
                + string.Join(", ", members.OrderBy(member => member.Name).Select(member =>
                    $"{member.Name} on {(member.ClaudeCodeConnectionId is { } id ? DomainId.Short(id) : "default")}")).EscapeMarkup()
                + "), so its members' sessions bill different Claude Code accounts. Bind every member to the same "
                + "connection with h9k project set <project> --claude-code-account <connection> to run them under it.")];
}
