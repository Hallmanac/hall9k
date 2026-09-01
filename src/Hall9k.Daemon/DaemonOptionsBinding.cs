using Microsoft.Extensions.Configuration;

namespace Hall9k.Daemon;

/// <summary>
/// What actually keeps <c>ConfigurationBinder</c> away from a <see cref="DaemonOptions"/> property
/// that resolves through <see cref="Hall9k.Domain.Infrastructure.Persistence.OperatingSettingsResolver"/>
/// instead: an <see langword="internal"/> setter alone does not do it, because
/// <c>ConfigurationBinder.BindProperty</c> converts a section's raw value before it ever checks
/// whether the property has a public setter to assign the result through — confirmed directly
/// against <c>ConfigurationBinder</c>: it throws converting <c>"four"</c> to <see cref="int"/>
/// regardless of the target property's setter visibility. Removing the key from the section a
/// generic <c>Bind()</c> call sees is the only way to actually stop the attempt (independent
/// pre-PR review, cycle 1, both lenses — the internal-setter claim on
/// <see cref="DaemonOptions.MaxConcurrentTaskRuns"/> and <see cref="DaemonOptions.SessionCapPerRun"/>
/// was wrong, and the still-public-setter <see cref="DaemonOptions.MaxConcurrentAgentSessions"/>
/// paid the same crash for a setting nothing reads any more).
/// </summary>
internal static class DaemonOptionsBinding
{
    /// <summary>
    /// The three concurrency settings <see cref="Hall9k.Domain.Infrastructure.Persistence.OperatingSettingsResolver"/>
    /// resolves on its own precedence walk, so a generic <c>Bind()</c> must never see them: two are
    /// then set by <c>PostConfigure</c> from that resolver's report, and the third
    /// (<see cref="DaemonOptions.MaxConcurrentAgentSessions"/>) is retired and read by nothing at
    /// all, so it is simply excluded rather than set again.
    /// </summary>
    internal static readonly string[] ResolverOwnedKeys =
    [
        nameof(DaemonOptions.MaxConcurrentTaskRuns),
        nameof(DaemonOptions.SessionCapPerRun),
        nameof(DaemonOptions.MaxConcurrentAgentSessions),
    ];

    /// <summary>
    /// A copy of <paramref name="section"/> with <paramref name="excludedKeys"/> removed, so a
    /// generic <c>Bind()</c> against the result never asks <c>ConfigurationBinder</c> to convert
    /// them.
    /// </summary>
    internal static IConfiguration ExcludingKeys(IConfigurationSection section, params string[] excludedKeys) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([.. section.AsEnumerable(makePathsRelative: true)
                .Where(pair => !excludedKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))])
            .Build();
}
