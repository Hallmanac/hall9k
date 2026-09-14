using JasperFx;
using Marten;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Repairs a schema that predates what <see cref="MartenConfiguration.ConfigureHall9k"/> now
/// configures, headlessly — no prompt, since there is nobody to ask. <c>h9k doctor</c> and
/// <c>h9k daemon start</c> already offer the same repair interactively (or via <c>--yes</c>), but
/// <c>h9kd</c> can also be launched directly by an OS autostart manager — a launchd LaunchAgent's
/// <c>RunAtLoad</c>, a Windows logon scheduled task — after a reboot, execing the binary itself
/// and never going through either of those. Every ordinary store this platform opens with
/// (<see cref="AutoCreate.CreateOnly"/>) refuses outright to alter an object already there, so an
/// install whose schema predates a change like enabling event metadata headers (idea 202383dc)
/// would otherwise crash raw on its first event append after an upgrade followed by a reboot, and
/// keep crashing on every autostart-driven restart until a human happened to run
/// <c>h9k doctor --yes</c> by hand (follow-up review finding, PR #370). Called once, before the
/// daemon's own store is opened with <see cref="AutoCreate.CreateOnly"/>, from
/// <c>Hall9k.Daemon</c>'s entry point directly — never from <c>Hall9k.Cli</c>, which the daemon
/// does not reference — so this lives beside the configuration it repairs rather than in the CLI's
/// own interactive <c>DatabaseDoctor</c>.
/// </summary>
public static class EventStoreSchemaGuard
{
    public static async Task EnsureCurrentAsync(string connectionString, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // IMartenStorage.ApplyAllConfiguredChangesToDatabaseAsync (the overload store.Storage
        // resolves to, distinct from Weasel's own IDatabase method of the same name) takes no
        // cancellation token — the same reason DatabaseDoctor.ApplySchemaAsync calls it bare.
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(connectionString);
            opts.ConfigureHall9k(AutoCreate.CreateOrUpdate);
        });
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
}
