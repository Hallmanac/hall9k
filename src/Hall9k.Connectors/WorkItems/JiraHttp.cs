using System.Net.Http.Headers;
using System.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One call to a Jira site. <see cref="Authorization"/> carries the credential, which is why
/// this record is never logged, never printed, and never quoted into a refusal — the provider
/// that builds it is the only thing that reads it.
/// </summary>
public sealed record JiraRequest(HttpMethod Method, Uri Url, string Authorization, string? JsonBody = null);

/// <summary>
/// What Jira answered: the status code and the body, both kept whole. The body is kept even for
/// a failure — especially for a failure — because Jira states the actual problem in it
/// (errorMessages), and a refusal that quoted only "403" would tell nobody which permission.
/// </summary>
public sealed record JiraResponse(int StatusCode, string Body);

/// <summary>
/// How the Jira connector reaches the network, as a delegate for the same reason
/// <see cref="Processes.ProcessRunner"/> is one: the provider needs exactly one verb, and a seam
/// this small is what lets its mapping and its refusals be tested against recorded Jira
/// responses instead of against a live Atlassian tenant.
/// </summary>
public delegate Task<JiraResponse> JiraRequester(JiraRequest request, CancellationToken cancellationToken);

public static class JiraHttp
{
    /// <summary>The real one.</summary>
    public static readonly JiraRequester Requester = SendAsync;

    /// <summary>
    /// How long a Jira call gets. Shorter than the process deadline the gh connector uses,
    /// because there is no interactive credential helper to wait for here: an HTTP call to
    /// Atlassian either answers in a second or is not going to. Long enough that a slow tenant
    /// under load is not reported as a broken one.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One client for the process. A per-call HttpClient exhausts sockets under any repetition,
    /// and the daemon's closeout sweep is repetition by design. The timeout lives on the client
    /// rather than in each call so the deadline cannot be forgotten at a call site.
    /// </summary>
    private static readonly HttpClient Client = new() { Timeout = Deadline };

    private static async Task<JiraResponse> SendAsync(JiraRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(request.Method, request.Url);
        // Set from the parsed value rather than by assigning the raw string, so a credential that
        // somehow carried a newline is rejected here instead of splicing a header into the request.
        message.Headers.Authorization = AuthenticationHeaderValue.Parse(request.Authorization);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (request.JsonBody is { } body)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Client.SendAsync(message, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        return new JiraResponse((int)response.StatusCode, content);
    }
}
