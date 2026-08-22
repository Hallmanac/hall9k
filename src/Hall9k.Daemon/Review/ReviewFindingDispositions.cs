namespace Hall9k.Daemon.Review;

/// <summary>
/// The one name for each heading the review loop writes into a cycle's merged findings
/// (Decisions Log #62). The engine writes each section under these strings and the fix prompt
/// points the agent at them by name, so the two are the same string rather than two spellings
/// that quietly drift until the instruction refers to a section that is no longer there.
/// <para>
/// The drift this prevents is silent by nature: renaming a group in the engine and not in the
/// prompt leaves a green build and a running loop, and only the fix session loses the sentence
/// that tells it which findings are its own — so a routed defect and this branch's own work
/// read the same to it.
/// </para>
/// </summary>
public static class ReviewFindingDispositions
{
    /// <summary>The section the groups below live under.</summary>
    public const string Heading = "What the platform decided about these findings";

    /// <summary>The group holding the findings that are this pull request's own work.</summary>
    public const string FixHere = "Fix in this pull request";

    /// <summary>The group holding pre-existing defects worth cleaning up here, each in its own commit.</summary>
    public const string FixHereInItsOwnCommit = "Fix in this pull request, in a commit of its own";

    /// <summary>The group holding the findings routed away to draft bug tasks.</summary>
    public const string DoNotFixHere = "Do NOT fix here — routed to draft bug tasks";
}
