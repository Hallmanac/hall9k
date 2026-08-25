namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>One role's configured model and where that value came from.</summary>
public sealed record RoleModelSetting(string Role, ResolvedSetting<string?> Model);

/// <summary>
/// Every daemon operating setting the CLI names directly, resolved the same way
/// <c>DaemonOptions</c> binds them at daemon startup: environment variable, then the platform
/// config file, then the built-in default (backlog 59). <see cref="ConfigFileMalformed"/> is
/// carried separately from "not configured", the same distinction
/// <see cref="ConnectionStringOrigin.PlatformConfigFileMalformed"/> makes for the connection
/// string — the fix is repairing the file, not the "nothing configured" guidance.
/// <see cref="UnusableEnvironmentVariables"/> is the same idea for a variable that is set but
/// fails to parse: the resolver falls through to a lower tier for the *value* it reports, but the
/// mistake itself has to survive into the report or an operator is told a healthy default is in
/// effect while the daemon dies at startup on the very variable that was silently discarded.
/// </summary>
public sealed record OperatingSettingsReport(
    ResolvedSetting<int> MaxConcurrentAgentSessions,
    ResolvedSetting<string> DefaultModel,
    IReadOnlyList<RoleModelSetting> ModelByRole,
    bool ConfigFileMalformed,
    IReadOnlyList<string> UnusableEnvironmentVariables);
