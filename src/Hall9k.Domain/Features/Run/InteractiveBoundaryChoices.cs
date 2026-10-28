using System.Globalization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The one place the choices at each of interactive mode's phase boundaries are written down, with
/// the exact command for each (task: a human at the wheel takes the fix role herself, fourth
/// criterion). Every surface that has to name them renders from here rather than restating them:
/// the park reason the review engine writes (<c>ReviewEngine.EnsureInteractiveProceedAsync</c>), the
/// outbound end-of-phase report a dispatched agent sends the human's registered session
/// (<c>WorkPromptBuilder.AppendOutboundMilestoneRules</c>), and the starting prompt
/// <c>h9k task drive</c> hands the operator's own session
/// (<c>WorkPromptBuilder.AppendInteractiveBoundaryChoices</c>). Defined in Domain for the same
/// reason <see cref="OutboundMilestone"/> is: both <c>Hall9k.Connectors</c> and
/// <c>Hall9k.Daemon</c> render it, and Domain is the only project both of them reference.
/// <para>
/// The point of centralizing it is that a human agent — a Claude Code session a human runs
/// interactively, in the seat — can offer the operator their choices in words without them
/// reading the docs, and offer the same ones the park text and the CLI's own <c>--help</c> offer.
/// Three surfaces each inventing their own wording is how one of them ends up naming three
/// choices where there are four.
/// </para>
/// <para>
/// Every rendered line addresses the operator in the third person plural or the second person,
/// never a guessed gender: this text ships into every interactive-mode task's prompts and park
/// reasons, and whoever is at the wheel of one of them is not someone this platform knows
/// anything about (AGENTS.md's own never-guess rule, applied to a person rather than an audit
/// field).
/// </para>
/// </summary>
public static class InteractiveBoundaryChoices
{
    /// <summary>
    /// Which levers apply at the boundary a run parked from, read off <see cref="RunAggregate.ParkedFromReviewPhase"/>
    /// (the boundary's own kind is never recorded; <c>ReviewEngine</c> only chooses it at its five
    /// park sites, and each of them parks from the phase named here). <see cref="ReviewPhase.FixNeeded"/>
    /// is the review-verdict-to-fix boundary, <see cref="ReviewPhase.MergeReady"/> the
    /// gates-to-pull-request one, and <see cref="ReviewPhase.None"/>, <see cref="ReviewPhase.Reverify"/>
    /// and <see cref="ReviewPhase.Settling"/> the proceed-or-redirect ones. Null for any other phase,
    /// which is not an interactive boundary at all (a dispute, a cap, a budget), so a caller never
    /// offers these choices at a park they do not answer.
    /// </summary>
    public static InteractiveBoundaryLevers? LeversFor(ReviewPhase parkedFrom) => parkedFrom switch
    {
        ReviewPhase.FixNeeded => InteractiveBoundaryLevers.ReviewVerdictToFix,
        ReviewPhase.MergeReady => InteractiveBoundaryLevers.GatesToPullRequest,
        ReviewPhase.None or ReviewPhase.Reverify or ReviewPhase.Settling => InteractiveBoundaryLevers.ProceedOrRedirect,
        _ => null,
    };

    /// <summary>
    /// One line per choice, each opening with the exact command, for a surface that renders a list
    /// (a prompt's bullets, a help topic). Ordered most-expected-first: at the review-verdict-to-fix
    /// boundary that is the fix session the reviewer's verdict already asked for, with the human's
    /// own fix immediately behind it.
    /// </summary>
    public static IReadOnlyList<string> Lines(InteractiveBoundaryLevers levers, Guid taskId)
    {
        string id = taskId.ToString("D", CultureInfo.InvariantCulture);
        return levers switch
        {
            InteractiveBoundaryLevers.ReviewVerdictToFix =>
            [
                $"`h9k review proceed {id}` — dispatch a headless fix session over the findings the "
                + "reviewers filed. This is the automatic path, just held for their go.",
                $"`h9k review fixed {id}` — the findings get fixed by hand, by them or by their driving "
                + "session on their word, and committed; this hands the branch back and the review agents "
                + "check that fix exactly as they would check a fix session's. No fix agent runs. Add "
                + "`--no-change \"<why>\"` if nothing was deliberately changed, which is otherwise refused.",
                $"`h9k review resolve {id} --needs-fixes \"<what to do instead>\"` — dispatch a fix session "
                + "carrying their redirect as its findings rather than the reviewer's own.",
                $"`h9k review resolve {id} --merge-ready --reason \"<why it is sound>\"` — overrule the "
                + "finding; the loop stops asking and moves on.",
                SkipLine(id),
            ],
            InteractiveBoundaryLevers.GatesToPullRequest =>
            [
                $"`h9k review proceed {id}` — open the pull request now. Interactive mode stays on, so they "
                + "keep the wheel for whatever the pull request needs next.",
                $"`h9k review resolve {id} --needs-fixes \"<what to change>\"` — send it back for a fix "
                + "instead of opening anything.",
                $"`h9k task revise {id} --clear-interactive-mode`, then `h9k review proceed {id}` once — "
                + "hand the pull request to the daemon and stop being asked: it shepherds the pull request "
                + "from there on its own (reviews, checks, follow-up laps, merge). An option, never the "
                + "default; interactive stays on unless they ask for this. Clearing the flag does not by "
                + "itself release this park, which is why the proceed still follows it.",
            ],
            _ =>
            [
                $"`h9k review proceed {id}` — continue exactly where the loop parked.",
                $"`h9k review resolve {id} --merge-ready` or `--needs-fixes \"<reason>\"` — redirect the "
                + "boundary instead of merely approving it.",
                SkipLine(id),
            ],
        };
    }

    /// <summary>
    /// The same choices as one sentence-shaped run, for the park reason the review engine records —
    /// which lands in <c>h9k status</c>, <c>h9k task show</c> and the outbound park notice, all of
    /// which are prose surfaces rather than bullet lists. Leads with the command names alone so the
    /// line stays readable at a glance; <see cref="Lines"/> is what carries the full explanations.
    /// </summary>
    public static string ParkText(InteractiveBoundaryLevers levers, Guid taskId)
    {
        string id = taskId.ToString("D", CultureInfo.InvariantCulture);
        return levers switch
        {
            InteractiveBoundaryLevers.ReviewVerdictToFix =>
                $"Five choices: h9k review proceed {id} (dispatch a fix session), "
                + $"h9k review fixed {id} (the findings were fixed by hand, by you or by your session on "
                + $"your word — commit first, then the review agents check that fix), h9k review resolve {id} --needs-fixes \"<redirect>\" (a fix session "
                + $"carrying your redirect), h9k review resolve {id} --merge-ready (overrule the finding), or "
                + $"{SkipParkClause(id)}.",
            InteractiveBoundaryLevers.GatesToPullRequest =>
                $"h9k review proceed {id} to open it, or h9k review resolve {id} --needs-fixes \"<reason>\" "
                + $"to send it back instead. To hand the pull request off entirely: h9k task revise {id} "
                + $"--clear-interactive-mode, then h9k review proceed {id} once — the daemon shepherds it "
                + "from there without you. Interactive mode is the default; that hand-off is an option.",
            _ => $"h9k review proceed {id} to continue, h9k review resolve to redirect it, or {SkipParkClause(id)}.",
        };
    }

    /// <summary>
    /// The skip-to-pr entry, shared by the two boundaries that offer it. The gates-to-pull-request
    /// boundary never does: review has already settled there, so nothing is left to skip, and
    /// <c>h9k review proceed</c> is the lever that opens the pull request.
    /// </summary>
    private static string SkipLine(string id) =>
        $"`h9k task skip-to-pr {id} --reason \"<why>\"` — open the pull request now and skip whatever review "
        + "is left: any gates still owed, every review track, the final pass, and the settling gate. Recorded on the "
        + "run as a manual override naming who skipped what. Only on their explicit word; `--no-reason` "
        + "records none.";

    private static string SkipParkClause(string id) =>
        $"h9k task skip-to-pr {id} (open the pull request now, skipping the rest of review; recorded as a "
        + "manual override)";
}
