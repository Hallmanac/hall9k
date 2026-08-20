namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// The CLI's door to the shared doorbell (Hall9k.Domain.Infrastructure.Persistence.Doorbell),
/// which it rings on the connection h9k is configured with.
/// </summary>
public static class Doorbell
{
    public const string Channel = Domain.Infrastructure.Persistence.Doorbell.Channel;

    public static Task RingAsync(string reason, CancellationToken cancellationToken) =>
        Domain.Infrastructure.Persistence.Doorbell.RingAsync(CliConfig.ConnectionString, reason, cancellationToken);
}
