using Hall9k.Connectors.Processes;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// A command-line tool that answers from a script instead of from the network: tests declare
/// what gh would have printed and then assert both the mapping and the arguments the connector
/// chose. Recorded output rather than a live account is the whole point — the refusal paths
/// (a missing issue, an unauthenticated CLI, a tool that never starts) are the ones hardest to
/// arrange for real and the ones a human most needs to read correctly.
/// </summary>
public sealed class RecordingProcessRunner(Func<ProcessResult> respond)
{
    /// <summary>Every (fileName, arguments, workingDirectory) the connector asked for, in order.</summary>
    public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

    public static RecordingProcessRunner Succeeding(string standardOutput) =>
        new(() => new ProcessResult(0, standardOutput, string.Empty));

    public static RecordingProcessRunner Failing(string standardError) =>
        new(() => new ProcessResult(1, string.Empty, standardError));

    /// <summary>
    /// The tool never ran at all: the operating system refused the spawn, so there is no exit
    /// code and no stderr to read — only the exception <c>Process.Start</c> threw.
    /// </summary>
    public static RecordingProcessRunner Unstartable(Exception refusal) => new(() => throw refusal);

    /// <summary>
    /// The tool started and then stopped answering: <see cref="ExternalProcess"/> waited out its
    /// deadline, killed the process tree, and reported that as a <see cref="TimeoutException"/>.
    /// A connector has to turn that into something a human can act on, and this is the only way
    /// to reach that path without a genuinely wedged gh.
    /// </summary>
    public static RecordingProcessRunner NeverAnswering() => new(() => throw new TimeoutException(
        "gh did not answer within 120 seconds, so Hall9k stopped waiting and ended it."));

    /// <summary>
    /// The tool exited — with <paramref name="exitCode"/> — and then something it started kept
    /// holding its output pipe open past the drain grace, so <see cref="ExternalProcess"/> never
    /// read the answer and reported <see cref="ProcessOutputStuckException"/> instead of a
    /// <see cref="ProcessResult"/>.
    /// </summary>
    public static RecordingProcessRunner ExitedButOutputStuck(int exitCode) => new(() => throw new ProcessOutputStuckException(
        exitCode,
        $"gh exited with code {exitCode}, but 5 seconds later something it started was still "
        + "holding its output open, so Hall9k never received the answer."));

    public ProcessRunner Runner => (fileName, arguments, workingDirectory, _) =>
    {
        Calls.Add((fileName, arguments, workingDirectory));
        return Task.FromResult(respond());
    };
}
