namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Reads the number out of a pull-request URL (https://github.com/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;).
/// URLs are not always canonical gh output — h9k task resolve --pr accepts a human-pasted
/// one — so the number survives trailing slashes and query/fragment noise. Anything
/// unparsable yields 0, never a guess (an honest "no number" the callers treat as absent).
/// </summary>
public static class PullRequestUrls
{
    public static int ParseNumber(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return 0;
        }

        string path = parsed.AbsolutePath.TrimEnd('/');
        return int.TryParse(path[(path.LastIndexOf('/') + 1)..], out int number) ? number : 0;
    }
}
