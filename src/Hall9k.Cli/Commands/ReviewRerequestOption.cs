using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The --rerequest-review option's vocabulary, shared by h9k owner set and h9k project set
/// so both levels of the chain read and refuse identically (Decisions Log #62). Unrecognized
/// input is rejected rather than silently read as "unset": a typo that quietly cleared the
/// setting would look exactly like turning it off.
/// </summary>
internal static class ReviewRerequestOption
{
    public static ReviewRerequestPolicy Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "enabled" or "true" or "yes" => ReviewRerequestPolicy.Enabled,
        "off" or "disabled" or "false" or "no" => ReviewRerequestPolicy.Disabled,
        "default" => ReviewRerequestPolicy.Unknown,
        _ => throw new DomainValidationException(
            $"--rerequest-review expects on, off, or default; got '{value}'. 'on' asks the pull "
            + "request's reviewers for another pass after a fix follow-up pushes; 'off' settles on the "
            + "internal review, the thread replies, and CI; 'default' clears this level so the ones "
            + "around it decide (Decisions Log #62)."),
    };

    /// <summary>How one level's setting reads in a show pane, including what an unset level defers to.</summary>
    public static string Describe(ReviewRerequestPolicy policy, string enabledDetail, string unsetDetail) =>
        policy == ReviewRerequestPolicy.Enabled
            ? $"[yellow]on[/] [dim]— {enabledDetail}[/]"
            : policy == ReviewRerequestPolicy.Disabled
                ? "[dim]off — pull requests settle on the internal review, the thread replies, and CI (log #62)[/]"
                : $"[dim]unset — {unsetDetail}[/]";
}
