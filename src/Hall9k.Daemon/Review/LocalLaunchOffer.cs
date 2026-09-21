using System.Text;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The identity a review report's launch offer carries (idea b9b09779, piece 5). A QA or design
/// review ends by asking the reviewer whether they would like the branch run for them; this is the
/// block underneath that question saying which branch, which checkout, and the one command that
/// answers it.
/// <para>
/// It exists because of what happens next. The reviewer says yes to their orchestrator window, in
/// a sentence, and that window has to resolve the yes to exactly one task, one run and one
/// worktree without asking them which — a person who has just read a findings report should not
/// have to go and find an id for the privilege of taking up an offer somebody made them. So the
/// report carries the identity rather than the window reconstructing it, and the command is
/// written out in full, ready to run.
/// </para>
/// <para>
/// Composed once for the whole report rather than once per persona: the QA and design sections
/// each write their own offer in their own words (the QA session's is genuinely its own prose, and
/// the design review's is <see cref="DesignReviewSection"/>'s), but there is only one branch under
/// review and one checkout of it, so two identity blocks would be two copies of one fact.
/// </para>
/// </summary>
public static class LocalLaunchOffer
{
    /// <summary>
    /// The block, or empty when no section of this report offered anything. Gated on the same
    /// <see cref="ReviewDriveDecision.CanOfferToRunItLive"/> the offers themselves are gated on, so
    /// an identity block can never appear under a report that made no offer, and an offer can never
    /// appear without the identity that answers it.
    /// <para>
    /// The gate is the run skill, and the run skill is not the whole of what the command needs: a
    /// review opened with <c>--no-worktree</c> has no checkout, and
    /// <c>h9k task run-local</c> refuses it with <see cref="LocalLaunchRefusal.NoWorktreeRecorded"/>.
    /// The offers above were written before that was known, so the block says the offer cannot be
    /// taken up here rather than printing a command it has already been decided will not run.
    /// </para>
    /// </summary>
    public static string Compose(
        ReviewPersonaPlan plan, Guid taskId, Guid runId, string worktreePath, string branch)
    {
        if (!plan.DriveDecisions.Any(decision => decision.CanOfferToRunItLive))
        {
            return string.Empty;
        }

        StringBuilder block = new();
        block.Append("\n## Running this branch locally\n");
        if (worktreePath.IsBlank())
        {
            block.Append(
                "\nA review above offered to run this branch for you, and this one cannot be taken "
                + "up. The offer was keyed on the project having a run skill, which it does; what "
                + "this review has not got is a checkout, because it was opened with --no-worktree "
                + "and read a deployed environment instead. There is no worktree to stand the branch "
                + "up in, and `h9k task run-local` refuses for exactly that reason, so nothing here "
                + "is worth running.\n");
            block.Append(
                $"\nRecorded identity: task `{taskId}`, run `{runId}`, branch `{Named(branch)}`, "
                + "worktree `not recorded`.\n");
            return block.ToString();
        }

        block.Append(
            "\nA review above offered to run this branch for you. If you want it, say so in your "
            + "orchestrator window and it runs this, which needs nothing else from you:\n");
        block.Append($"\n    h9k task run-local {taskId}\n");
        block.Append(
            $"\nIt stands the branch up in this review's own checkout ({Named(worktreePath)}) by the "
            + "project's run skill, on an ephemeral port wherever the launch command has somewhere to "
            + "put one, and prints the address plus every step only a person can do. A step that needs "
            + "you stops it and says what to do; `--continue` picks up at the next one, and `--stop` "
            + "ends it. Nothing starts until you ask.\n");
        block.Append(
            $"\nRecorded identity: task `{taskId}`, run `{runId}`, branch `{Named(branch)}`, worktree "
            + $"`{Named(worktreePath)}`.\n");
        return block.ToString();
    }

    /// <summary>
    /// A value that is genuinely missing said as missing rather than printed as an empty pair of
    /// backticks: a reader given a blank cannot tell a fact nobody recorded from a field the
    /// platform failed to fill in. The missing worktree gets a whole paragraph of its own above,
    /// because it decides whether the offer can be taken up at all; the rest of the identity is
    /// this.
    /// </summary>
    private static string Named(string value) => value.IsNotBlank() ? value : "not recorded";
}
