namespace Hall9k.Daemon.Review;

/// <summary>
/// The one name for the disposition block the review loop writes into a cycle's merged findings
/// (Decisions Log #62). The engine writes the section under this heading and the fix prompt
/// points the agent at it by name, so the two are the same string rather than two spellings
/// that quietly drift until the instruction refers to a section that is no longer there.
/// </summary>
public static class ReviewFindingDispositions
{
    public const string Heading = "What the platform decided about these findings";
}
