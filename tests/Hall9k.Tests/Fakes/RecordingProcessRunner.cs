using Hall9k.Connectors.Processes;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// A command-line tool that answers from a script instead of from the network: tests declare
/// what gh would have printed and then assert both the mapping and the arguments the connector
/// chose. Recorded output rather than a live account is the whole point — the refusal paths
/// (a missing issue, an unauthenticated CLI, a tool that never starts) are the ones hardest to
/// arrange for real and the ones a human most needs to read correctly.
/// </summary>
public sealed class RecordingProcessRunner(Func<IReadOnlyList<string>, ProcessResult> respond)
{
    /// <summary>Every (fileName, arguments, workingDirectory) the connector asked for, in order.</summary>
    public List<(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

    /// <summary>Convenience constructor for a fake that answers the same way whatever it is asked.</summary>
    public RecordingProcessRunner(Func<ProcessResult> respond) : this(_ => respond())
    {
    }

    public static RecordingProcessRunner Succeeding(string standardOutput) =>
        new(() => new ProcessResult(0, standardOutput, string.Empty));

    /// <summary>
    /// A fake whose answer depends on the arguments it was called with — twg's own shape, where
    /// a create and its own read-back search need different JSON, unlike gh's single-call tests.
    /// </summary>
    public static RecordingProcessRunner RespondingTo(Func<IReadOnlyList<string>, ProcessResult> respond) => new(respond);

    public static RecordingProcessRunner Failing(string standardError) =>
        new(() => new ProcessResult(1, string.Empty, standardError));

    /// <summary>
    /// twg's own way of reporting a runtime failure, including an expired or missing login: the
    /// JSON error envelope goes to stdout, exit code 77 for an auth refusal, and stderr is left
    /// empty (verified live against an installed twg, independent pre-PR review, cycle 3) — a
    /// shape <see cref="Failing"/> cannot model, since that puts the refusal on stderr the way
    /// most other tools this fake stands in for (gh, Docker) actually do.
    /// </summary>
    public static RecordingProcessRunner FailingWithEnvelope(int exitCode, string errorCode, string message) =>
        new(() => new ProcessResult(
            exitCode, $"{{\"error\":{{\"code\":\"{errorCode}\",\"message\":\"{message}\"}}}}", string.Empty));

    /// <summary>twg's own shape for an expired or missing login: exit 77, AUTH_REQUIRED, empty stderr.</summary>
    public static RecordingProcessRunner TwgAuthExpired() =>
        FailingWithEnvelope(77, "AUTH_REQUIRED", "authentication required");

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
        return Task.FromResult(respond(arguments));
    };

    /// <summary>
    /// A <see cref="ProcessRunner"/> for a test that constructs a real connector (e.g.
    /// <c>GitHubWorkItemProvider</c>) but only ever exercises a synchronous, no-process member of
    /// it — <c>WebUrl</c>, chiefly. Passing this explicitly rather than leaving the constructor's
    /// <c>ProcessRunner? runner = null</c> default to resolve to <see cref="ExternalProcess.Runner"/>
    /// turns "this test never happens to call anything that shells out" from an unstated fact
    /// about which methods are exercised into an enforced one: if a later edit calls a method that
    /// does shell out, this throws immediately instead of the test silently reaching the real
    /// <c>gh</c> and the real network (origin incident, 2026-08-29 — see
    /// <c>ProcessTerminationGuardTests</c>' own doc comment for the crash this class of gap can
    /// produce). "Loud" is not guaranteed everywhere: a caller that wraps its runner call in a
    /// broad best-effort <c>catch</c> — <c>GitHubWorkItemProvider.TryObserveRepositoryHostAsync</c>
    /// is one — swallows this exception the same as any other and degrades silently instead of
    /// failing the test; the guarantee this fake gives is that the real process is never spawned,
    /// not that every caller surfaces the throw.
    /// <para>
    /// The refusal quotes only the tool name and the argument count, never the arguments
    /// themselves, the same reason <see cref="FakeJiraRequester.NeverInvoked"/> quotes only a
    /// request's method and URL: a test that trips this guard reports what it asked for without
    /// risking a credential passed on the command line spilling into the test log.
    /// </para>
    /// </summary>
    public static ProcessRunner NeverInvoked() => (fileName, arguments, _, _) => throw new InvalidOperationException(
        $"this test's ProcessRunner was not expected to be called, but was invoked with '{fileName}' " +
        $"and {arguments.Count} argument(s) — this test only exercises members that must never shell " +
        "out; if that changed, give it a real fake instead of this guard");
}
