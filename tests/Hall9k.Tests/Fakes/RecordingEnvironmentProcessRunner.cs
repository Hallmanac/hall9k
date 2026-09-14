using Hall9k.Connectors.Processes;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// The <see cref="EnvironmentProcessRunner"/> twin of <see cref="RecordingProcessRunner"/> — a
/// command-line tool that answers from a script instead of a real gh/network call, recording every
/// (fileName, arguments, workingDirectory, environment) it was asked for so a test can assert both
/// the mapping and, when it matters, that a pinned token actually reached the child process as
/// <c>GH_TOKEN</c> rather than the machine's own <c>gh auth</c> selection.
/// </summary>
public sealed class RecordingEnvironmentProcessRunner(Func<IReadOnlyList<string>, ProcessResult> respond)
{
    public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];

    public RecordingEnvironmentProcessRunner(Func<ProcessResult> respond) : this(_ => respond())
    {
    }

    public static RecordingEnvironmentProcessRunner Succeeding(string standardOutput) =>
        new(() => new ProcessResult(0, standardOutput, string.Empty));

    public static RecordingEnvironmentProcessRunner Failing(string standardError) =>
        new(() => new ProcessResult(1, string.Empty, standardError));

    public EnvironmentProcessRunner Runner => (fileName, arguments, workingDirectory, environment, _) =>
    {
        Calls.Add((fileName, arguments, workingDirectory, environment));
        return Task.FromResult(respond(arguments));
    };
}
