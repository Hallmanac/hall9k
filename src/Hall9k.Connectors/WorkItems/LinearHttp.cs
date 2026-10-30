using System.Net.Http.Headers;
using System.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One GraphQL call to Linear. <see cref="Authorization"/> carries the API key itself, which is why
/// this record is never logged, never printed, and never quoted into a refusal: the provider that
/// builds it is the only thing that reads it.
/// </summary>
public sealed record LinearRequest(Uri Url, string Authorization, string JsonBody);

/// <summary>
/// What Linear answered: the status code and the body, both kept whole, plus the
/// <c>Retry-After</c> header when there was one. The body is kept for a failure too, because a
/// GraphQL error is carried in it and a refusal that quoted only the status would hide the message.
/// </summary>
public sealed record LinearResponse(int StatusCode, string Body, string? RetryAfter = null);

/// <summary>
/// How the Linear connector reaches the network, as a delegate for the same reason
/// <see cref="JiraRequester"/> is one: the provider needs exactly one verb, and a seam this small is
/// what lets its mapping and its refusals be tested against fixture responses written from
/// Linear's public schema instead of a live workspace.
/// </summary>
public delegate Task<LinearResponse> LinearRequester(LinearRequest request, CancellationToken cancellationToken);

public static class LinearHttp
{
    /// <summary>Linear's single GraphQL endpoint.</summary>
    public static readonly Uri Endpoint = new("https://api.linear.app/graphql");

    /// <summary>The real one.</summary>
    public static readonly LinearRequester Requester = SendAsync;

    /// <summary>
    /// How long a Linear call gets, for the reason <see cref="JiraHttp.Deadline"/> is thirty
    /// seconds: an HTTP call to a hosted API either answers quickly or is not going to.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Client = new() { Timeout = Deadline };

    private static async Task<LinearResponse> SendAsync(LinearRequest request, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, request.Url);
        // A personal API key goes in the header as it is, with no scheme in front (Bearer is for
        // OAuth tokens). The add-without-validation path is deliberate, since a key is not a
        // "scheme parameter" pair, and the newline check is what stops a credential that somehow
        // carried one from splicing a header into the request.
        if (request.Authorization.AsSpan().IndexOfAny('\r', '\n') >= 0
            || !message.Headers.TryAddWithoutValidation("Authorization", request.Authorization))
        {
            throw new HttpRequestException("The Linear API key is not usable as an Authorization header value.");
        }

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Client.SendAsync(message, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        string? retryAfter = response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;
        return new LinearResponse((int)response.StatusCode, content, retryAfter);
    }
}
