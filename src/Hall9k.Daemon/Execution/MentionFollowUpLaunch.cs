namespace Hall9k.Daemon.Execution;

/// <summary>
/// What <see cref="RunLauncher.LaunchPrReviewMentionFollowUpAsync"/> did with the claim it was
/// handed. <see cref="SkipReason"/> is set only when the stored comment was refused before any
/// session launched (and the claim given back); every other ending, including a launch that
/// failed and was recorded as such, is <see cref="Handled"/>.
/// </summary>
public sealed record MentionFollowUpLaunch(string? SkipReason)
{
    public static readonly MentionFollowUpLaunch Handled = new((string?)null);

    public bool Skipped => SkipReason is not null;
}
