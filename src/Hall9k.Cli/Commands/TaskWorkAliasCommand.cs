namespace Hall9k.Cli.Commands;

/// <summary>
/// <c>h9k task work</c>, the old name of <see cref="TaskDriveCommand"/>, kept for one release as a
/// hidden alias. It is the same command in every respect (the same <c>Settings</c> type, so the
/// dispatched-session refusal and every option bind identically); the only difference is one line on
/// stderr saying the verb was renamed. Stdout and the exit code are untouched, so scripts keep working.
/// </summary>
public sealed class TaskWorkAliasCommand : TaskDriveCommand
{
    public const string RenamedNotice = "h9k task work is renamed h9k task drive; work will be removed in a later release.";

    protected override Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken) =>
        RunAsync(Console.Error, () => base.ExecuteAsync(settings, cancellationToken), cancellationToken);

    /// <summary>Says the verb was renamed before anything else happens, then returns the drive's own exit code.</summary>
    internal static async Task<int> RunAsync(TextWriter error, Func<Task<int>> drive, CancellationToken cancellationToken)
    {
        await error.WriteLineAsync(RenamedNotice.AsMemory(), cancellationToken);

        return await drive();
    }
}
