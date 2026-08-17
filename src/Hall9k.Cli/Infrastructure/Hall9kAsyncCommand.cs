using Spectre.Console.Cli;

namespace Hall9k.Cli.Infrastructure;

/// <summary>Base command narrowing Spectre's signature to (settings, cancellationToken).</summary>
public abstract class Hall9kAsyncCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    protected sealed override Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken cancellationToken) =>
        ExecuteAsync(settings, cancellationToken);

    protected abstract Task<int> ExecuteAsync(TSettings settings, CancellationToken cancellationToken);
}
