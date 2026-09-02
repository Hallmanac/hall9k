using Hall9k.Connectors.WorkItems;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// The HTTP-side counterpart to <see cref="RecordingProcessRunner"/>: records every
/// <see cref="JiraRequest"/> a <see cref="JiraWorkItemProvider"/> or a
/// <see cref="JiraWriteExecutor"/> sends and answers each one from a caller-supplied function of
/// the request itself, so a single fake can distinguish a create from its own read-back
/// verification, or a search from a candidate confirmation, by method and URL rather than
/// answering every call identically.
/// </summary>
public sealed class RecordingJiraRequester(Func<JiraRequest, JiraResponse> respond)
{
    public List<JiraRequest> Requests { get; } = [];

    public JiraRequester Requester => (request, _) =>
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    };

    /// <summary>Every call answers identically — the simplest shape, for a test that only cares about one call.</summary>
    public static RecordingJiraRequester Succeeding(int statusCode, string body) =>
        new(_ => new JiraResponse(statusCode, body));

    /// <summary>The general shape: the response depends on what was actually asked for.</summary>
    public static RecordingJiraRequester RespondingTo(Func<JiraRequest, JiraResponse> respond) => new(respond);
}
