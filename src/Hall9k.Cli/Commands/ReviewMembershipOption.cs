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

    /// <summary>
    /// How the effective value reads in a show pane, including what an unset level defers to.
    /// <paramref name="isPrivate"/> is the project's own last-observed <c>ProjectRepositoryVisibility</c>
    /// (independent pre-PR review, cycle 1, conformance lens) — null before any sweep has ever
    /// observed it — so an unset row states the CURRENT effective value directly rather than
    /// leaving the reader to combine this row with the separate visibility row below it to learn
    /// whether the gate is actually on right now. This is the daemon's own <c>gateOn = explicitSetting
    /// ?? isPrivate != true</c> formula (<c>AutoPrReviewObservation.DecideMembershipGate</c>)
    /// applied to the last observation this command can read without a live <c>gh</c> call of its
    /// own — a sweep that has not observed visibility yet, or whose most recent read failed, reads
    /// the identical fail-closed default the gate itself falls back to on a failed read: on.
    /// </summary>
    public static string Describe(ReviewMembershipPolicy policy, bool? isPrivate, string enabledDetail, string unsetDetail)
    {
        if (policy == ReviewMembershipPolicy.Enabled)
        {
            return $"[yellow]on[/] [dim]— {enabledDetail}[/]";
        }

        if (policy == ReviewMembershipPolicy.Disabled)
        {
            return "[dim]off — every request or mention runs unattended, the collaborator behaviour (security review idea 6be68ee2)[/]";
        }

        string effective = isPrivate == true
            ? "off — the last observed visibility is private or internal"
            : "on — the fail-closed default applies, whether because the last observed visibility is "
              + "public or because no successful read exists yet; a failed read on any later sweep "
              + "fails closed the same way, whatever this row shows";
        return $"[dim]unset, currently {effective} — {unsetDetail}[/]";
    }
}
