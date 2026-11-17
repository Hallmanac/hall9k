using System.Text.Json;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.ClaudeCode;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Spectre.Console;

namespace Hall9k.Cli.Diagnostics;

/// <summary>What one bound connection's check came to, with the status it read when it read one.</summary>
internal sealed record ClaudeCodeDoctorProbe(ClaudeAccountCheckResult? Result, string? Failure)
{
    public bool Passed => Result is { Passed: true };
}

/// <summary>
/// What <see cref="ClaudeCodeDoctor"/>'s section is composed from, so a test can supply the connection list,
/// the daemon's pid record, the aliveness check, doctor's own environment, and the account check.
/// </summary>
internal sealed record ClaudeCodeDoctorInputs(
    IReadOnlyList<ConnectionDetails> Connections,
    IReadOnlyList<ProjectDetails> Projects,
    DaemonPidRecord? DaemonRecord,
    Func<int, DateTimeOffset, bool> IsAlive,
    Func<string, string?> ReadShellEnvironment,
    Func<ClaudeCodeAccount, CancellationToken, Task<ClaudeAccountCheckResult>> Check);

/// <summary>
/// <c>h9k doctor</c>'s section for claude-code connections, shaped like <see cref="LinearDoctor"/>. It checks
/// each connection a project is bound to the way a spawn would, printing the account it found or the problem and its fix; warns about a credential that outranks a
/// directory's login, in this shell's environment or in a bound directory's <c>settings.json</c>; and warns
/// when a solution's members run under different accounts. Whether or not a connection is recorded, it also
/// warns when the running daemon or this shell carries CLAUDE_CONFIG_DIR. No failure here stops the rest of
/// the doctor run.
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
        IReadOnlyList<ProjectDetails> projects = connections.Count == 0
            ? []
            : await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        await WriteAsync(
            new ClaudeCodeDoctorInputs(
                connections,
                projects,
                DaemonPidFile.TryReadRecord(DaemonRuntime.PidFile),
                DaemonProcess.IsAlive,
                Environment.GetEnvironmentVariable,
                (account, token) => CliClaudeAccount.Checker.CheckAsync(account, reusePass: false, token).AsTask()),
            AnsiConsole.MarkupLine,
            cancellationToken);
    }

    /// <summary>
    /// The section composed from its inputs, writing each markup line to <paramref name="write"/> as it is
    /// ready. The two CLAUDE_CONFIG_DIR warnings print whether or not a connection is recorded, since a node
    /// with none is exactly where an inherited value silently stops applying; the rest needs a connection.
    /// </summary>
    internal static async Task WriteAsync(ClaudeCodeDoctorInputs inputs, Action<string> write, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> configDirectoryWarnings =
        [
            .. DaemonConfigDirectoryLines(inputs.DaemonRecord, inputs.IsAlive),
            .. ShellConfigDirectoryLines(inputs.ReadShellEnvironment),
        ];
        if (inputs.Connections.Count == 0 && configDirectoryWarnings.Count == 0)
        {
            return;
        }

        write("[bold]Claude Code accounts[/]");
        foreach (string warning in configDirectoryWarnings)
        {
            write(warning);
        }

        if (inputs.Connections.Count == 0)
        {
            return;
        }

        foreach (string variable in EnvironmentOverrides(inputs.ReadShellEnvironment))
        {
            write(
                $"[yellow]  {variable} is set in this shell[/], and it outranks every config directory's own login, so a "
                + "bound account's check reports it instead of the account and refuses the session. Unset it in the "
                + "environment h9k and h9kd run in.");
        }

        foreach (ConnectionDetails connection in inputs.Connections)
        {
            string[] bound = [.. inputs.Projects.Where(project => project.ClaudeCodeConnectionId == connection.Id).Select(project => project.Name).Order()];
            if (bound.Length == 0)
            {
                write(
                    $"[dim]  {DomainId.Short(connection.Id)} {connection.ExternalAccountId.EscapeMarkup()}: no project is "
                    + "bound to it, so it is not checked.[/]");
                continue;
            }

            ClaudeCodeAccount account = ClaudeCodeAccount.For(connection);
            ClaudeCodeDoctorProbe probe = await ProbeAsync(() => inputs.Check(account, cancellationToken), cancellationToken);
            write(ProbeLine(connection, bound, probe));

            foreach (string setting in await SettingsOverridesAsync(connection.ExternalAccountId, cancellationToken))
            {
                write(
                    $"[yellow]  {connection.ExternalAccountId.EscapeMarkup()}'s settings.json sets {setting.EscapeMarkup()}[/], "
                    + "which outranks the directory's own login for every session started under it. Remove it so "
                    + "the check can confirm the account.");
            }
        }

        foreach (string warning in SolutionMismatches(inputs.Projects))
        {
            write(warning);
        }
    }

    /// <summary>
    /// What the running daemon inherited of CLAUDE_CONFIG_DIR, read from its pid file: a warning when it
    /// carries a value, one line saying doctor cannot tell when a running daemon recorded nothing (an older
    /// build wrote the file), and nothing when it inherited none or when no daemon is running. The record
    /// is trusted only once <paramref name="isAlive"/> confirms the pid and start time it names, so a file a
    /// daemon left behind when it exited never warns.
    /// </summary>
    internal static IReadOnlyList<string> DaemonConfigDirectoryLines(
        DaemonPidRecord? record, Func<int, DateTimeOffset, bool> isAlive) =>
        record is null || !isAlive(record.Daemon.ProcessId, record.Daemon.StartedAt)
            ? []
            : record.Inherited switch
            {
                null =>
                [
                    $"[yellow]  h9kd is running but recorded nothing about the {ClaudeConfigDirectory.Variable} it inherited[/] "
                    + "(it was started from a build that predates the record), so doctor cannot tell whether it carries "
                    + "one. Restart the daemon (h9k daemon stop, then h9k daemon start) so doctor can tell.",
                ],
                { ClaudeConfigDirectory: { } value } =>
                [
                    $"[yellow]  h9kd is running with {ClaudeConfigDirectory.Variable}={ExternalText.OneLineMarkup(value)} "
                    + "inherited from the environment it started in[/]. Every session it starts under the default "
                    + "login (for a project with no bound claude-code account, or for a solution whose members do not "
                    + "all share one) runs under the default login regardless of that value, while a session for a "
                    + "bound project runs under its own connection's directory. To run projects under that directory, "
                    + $"register it with h9k connection add claude-code --config-dir \"{ExternalText.OneLineMarkup(value)}\" "
                    + "and bind each project that should use it with h9k project set <project> --claude-code-account "
                    + "<connection>; otherwise remove the variable from the daemon's environment and restart the "
                    + "daemon (h9k daemon stop, then h9k daemon start from a shell without it).",
                ],
                _ => [],
            };

    /// <summary>
    /// A warning when the shell doctor runs in carries a non-empty CLAUDE_CONFIG_DIR. h9k's own spawns under
    /// the default login remove it, but a printed line with no account prefix pasted here runs under it, and
    /// a daemon started from here inherits it. The commands it names are examples, not the full list.
    /// </summary>
    internal static IReadOnlyList<string> ShellConfigDirectoryLines(Func<string, string?> read) =>
        read(ClaudeConfigDirectory.Variable) is { Length: > 0 } value
            ?
            [
                $"[yellow]  {ClaudeConfigDirectory.Variable}={ExternalText.OneLineMarkup(value)} is set in this shell[/]. "
                + "Every Claude Code session h9k itself starts from this shell under the default login (for example "
                + "h9k task start, h9k task delegate, h9k task drive --direct-launch and h9k orchestrator measure) "
                + "runs under the default login regardless of it. A line h9k prints for a project with no bound "
                + "account, or for this node's own window, carries no account, so pasted into this shell it runs "
                + "under the directory the variable names, and a daemon started directly from this shell inherits "
                + "the variable. Unset it before pasting such a line or starting the daemon.",
            ]
            : [];

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
