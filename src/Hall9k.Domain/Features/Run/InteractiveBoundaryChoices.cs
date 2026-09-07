using System.Globalization;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The one place the choices at each of interactive mode's phase boundaries are written down, with
/// the exact command for each (task: a human at the wheel takes the fix role herself, fourth
/// criterion). Every surface that has to name them renders from here rather than restating them:
/// the park reason the review engine writes (<c>ReviewEngine.EnsureInteractiveProceedAsync</c>), the
/// outbound end-of-phase report a dispatched agent sends the human's registered session
/// (<c>WorkPromptBuilder.AppendOutboundMilestoneRules</c>), and the starting prompt
/// <c>h9k task work</c> hands the operator's own session
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
                $"`h9k review fixed {id}` — they fix the findings themselves in the worktree and commit; "
                + "this hands the branch back and the review agents check that fix exactly as they would "
                + "check a fix session's. No fix agent runs. Add `--no-change \"<why>\"` if they "
                + "deliberately changed nothing, which is otherwise refused.",
                $"`h9k review resolve {id} --needs-fixes \"<what to do instead>\"` — dispatch a fix session "
                + "carrying their redirect as its findings rather than the reviewer's own.",
                $"`h9k review resolve {id} --merge-ready --reason \"<why it is sound>\"` — overrule the "
                + "finding; the loop stops asking and moves on.",
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
                $"Four choices: h9k review proceed {id} (dispatch a fix session), "
                + $"h9k review fixed {id} (you fixed it yourself — commit first, then the review agents "
                + $"check your fix), h9k review resolve {id} --needs-fixes \"<redirect>\" (a fix session "
                + $"carrying your redirect), or h9k review resolve {id} --merge-ready (overrule the finding).",
            InteractiveBoundaryLevers.GatesToPullRequest =>
                $"h9k review proceed {id} to open it, or h9k review resolve {id} --needs-fixes \"<reason>\" "
                + $"to send it back instead. To hand the pull request off entirely: h9k task revise {id} "
                + $"--clear-interactive-mode, then h9k review proceed {id} once — the daemon shepherds it "
                + "from there without you. Interactive mode is the default; that hand-off is an option.",
            _ => $"h9k review proceed {id} to continue, or h9k review resolve to redirect it.",
        };
    }
}
