using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Connection;

namespace Hall9k.Connectors.ClaudeCode;

/// <summary>
/// What one <c>claude auth status</c> read produced: the parsed status, or why there is none. A probe reports
/// an outcome for every case and never throws, so a caller can always print something.
/// </summary>
public sealed record ClaudeAuthStatusRead(ClaudeAuthStatus? Status, string? Failure)
{
    public static ClaudeAuthStatusRead Failed(string failure) => new(null, failure);
}

/// <summary>
/// Runs <c>claude auth status</c> with <c>CLAUDE_CONFIG_DIR</c> set to a directory, using the binary and the
/// environment a spawn would get. Only call it for a directory that exists: Claude Code creates a missing one.
/// </summary>
public interface IClaudeAuthStatusProbe
{
    Task<ClaudeAuthStatusRead> ReadAsync(string configDirectory, CancellationToken cancellationToken);
}

/// <summary>The real probe: the process environment plus <c>CLAUDE_CONFIG_DIR</c>, and the JSON read whatever the exit code.</summary>
public sealed class ClaudeAuthStatusProbe(EnvironmentProcessRunner? runner = null) : IClaudeAuthStatusProbe
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private readonly EnvironmentProcessRunner _runner = runner ?? ExternalProcess.RunnerWithEnvironmentAndDeadline(Deadline);

    public async Task<ClaudeAuthStatusRead> ReadAsync(string configDirectory, CancellationToken cancellationToken)
    {
        string binary = ClaudeCodeBinary.Resolve();
        ProcessResult result;
        try
        {
            result = await _runner(
                binary, ["auth", "status"], configDirectory,
                new Dictionary<string, string> { [ClaudeConfigDirectory.Variable] = configDirectory },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ClaudeAuthStatusRead.Failed($"'{binary} auth status' could not run: {exception.Message}");
        }

        // Logged out exits 1 with the same JSON, so the exit code decides nothing here.
        return ClaudeAuthStatus.Parse(result.StandardOutput) is { } status
            ? new ClaudeAuthStatusRead(status, null)
            : ClaudeAuthStatusRead.Failed(
                $"'{binary} auth status' exited {result.ExitCode} without its JSON status"
                + (result.StandardError.Trim() is { Length: > 0 } error ? $": {error}" : "."));
    }
}

/// <summary>The binary Hall9k spawns as Claude Code: <c>HALL9K_CLAUDE_PATH</c> when set, else <c>claude</c> on the PATH.</summary>
public static class ClaudeCodeBinary
{
    public const string OverrideVariable = "HALL9K_CLAUDE_PATH";

    public static string Resolve() =>
        Environment.GetEnvironmentVariable(OverrideVariable) ?? "claude";
}
