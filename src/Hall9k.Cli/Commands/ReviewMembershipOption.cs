using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The <c>--review-requires-membership</c> option's vocabulary (security review idea 6be68ee2,
/// finding 1), the <see cref="ReviewRerequestOption"/> convention: unrecognized input is refused
/// rather than silently read as 'unset', so a typo can never quietly turn the gate off on a public
/// repository.
/// </summary>
internal static class ReviewMembershipOption
{
    public static ReviewMembershipPolicy Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "enabled" or "true" or "yes" => ReviewMembershipPolicy.Enabled,
        "off" or "disabled" or "false" or "no" => ReviewMembershipPolicy.Disabled,
        "default" => ReviewMembershipPolicy.Unknown,
        _ => throw new DomainValidationException(
            $"--review-requires-membership expects on, off, or default; got '{value}'. 'on' requires a "
            + "hall9k team member's request or mention whatever the repository's own visibility reads "
            + "as; 'off' never requires one, the collaborator behaviour a private or internal repository "
            + "already has by default; 'default' clears this override so the daemon decides fresh every "
            + "sweep from the repository's own visibility (security review idea 6be68ee2, finding 1)."),
    };

    /// <summary>How the effective value reads in a show pane, including what an unset level defers to.</summary>
    public static string Describe(ReviewMembershipPolicy policy, string enabledDetail, string unsetDetail) =>
        policy == ReviewMembershipPolicy.Enabled
            ? $"[yellow]on[/] [dim]— {enabledDetail}[/]"
            : policy == ReviewMembershipPolicy.Disabled
                ? "[dim]off — every request or mention runs unattended, the collaborator behaviour (security review idea 6be68ee2)[/]"
                : $"[dim]unset — {unsetDetail}[/]";
}
