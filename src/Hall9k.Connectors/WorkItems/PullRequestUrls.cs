namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Reads the number out of a pull-request URL (https://github.com/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;).
/// URLs are not always canonical gh output — h9k task resolve --pr accepts a human-pasted
/// one — so the number survives trailing slashes and query/fragment noise. Anything
/// unparsable yields 0, never a guess (an honest "no number" the callers treat as absent).
/// The path's second-to-last segment must literally be "pull" — an otherwise well-formed
/// GitHub URL ending in a number, such as an issue (.../issues/24), does not name a pull
/// request and must not be read as one (adversarial review, cycle 1: a human-pasted --pr
/// naming an issue silently enrolled that issue's number as a run's merge signal).
/// </summary>
public static class PullRequestUrls
{
    public static int ParseNumber(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return 0;
        }

        string[] segments = parsed.AbsolutePath.Trim('/').Split('/');
        return segments is [.., "pull", string numberSegment] && int.TryParse(numberSegment, out int number)
            ? number
            : 0;
    }
}
