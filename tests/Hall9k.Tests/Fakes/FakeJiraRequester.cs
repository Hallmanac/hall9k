using Hall9k.Connectors.WorkItems;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// The Jira-side counterpart to <see cref="RecordingProcessRunner"/>'s never-invoked runner, kept
/// here rather than privately in one test class because more than one place in the tree builds a
/// real <see cref="JiraWorkItemProvider"/> (directly, or indirectly through
/// <c>WorkItemConnections.ImporterAsync</c>) while only ever exercising members that must not
/// reach Atlassian.
/// </summary>
public static class FakeJiraRequester
{
    /// <summary>
    /// A <see cref="JiraRequester"/> for a test that constructs a real
    /// <see cref="JiraWorkItemProvider"/> but only ever exercises a synchronous, no-request member
    /// of it — <c>WebUrl</c>, chiefly — or a path that refuses before any request is built.
    /// Passing this explicitly rather than leaving the constructor's <c>requester = null</c>
    /// default resolve to <see cref="JiraHttp.Requester"/> turns "this test never happens to call
    /// anything that reaches Jira" from an unstated fact into an enforced one, the same reason
    /// <see cref="RecordingProcessRunner.NeverInvoked"/> exists for the gh side.
    /// <para>
    /// The refusal quotes only the request's method and URL, never its
    /// <see cref="JiraRequest.Authorization"/>, so a test that trips this guard reports what it
    /// asked for without spilling the credential it asked with.
    /// </para>
    /// </summary>
    public static JiraRequester NeverInvoked() => (request, _) => throw new InvalidOperationException(
        "this test's JiraRequester was not expected to be called, but was invoked with " +
        $"'{request.Method} {request.Url}' — this test only exercises members that must never reach " +
        "the network; if that changed, give it a real fake instead of this guard");
}
