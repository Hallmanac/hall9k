using System.Globalization;

namespace Hall9k.Domain.Features.Run;

/// <summary>What the daemon found when it checked a branch against the tip the human gate parked it at.</summary>
public enum HumanGateFindingKind
{
    /// <summary>The branch is where the park found it and the worktree holds nothing uncommitted.</summary>
    Unchanged,

    /// <summary>Commits landed on the branch since the park, so nothing has read them.</summary>
    TipMoved,

    /// <summary>The branch tip could not be read, or the park never recorded one to compare with.</summary>
    TipUnobservable,

    /// <summary>The worktree holds uncommitted work, which no pull request would carry.</summary>
    UncommittedWork,

    /// <summary>The worktree's status could not be read.</summary>
    StatusUnobservable,
}

/// <summary>
/// The human gate's moved-tip decision as a pure function over observed facts, so it can be pinned in a
/// test without a branch, a worktree, or a daemon. <c>h9k review proceed</c> may be typed on a machine
/// that holds no worktree, which is why the check lives with the daemon and not the command; the daemon
/// reads the facts and asks this.
/// <para>
/// Anything it could not observe counts against the gate rather than for it: an unreadable tip or status is
/// reported as such and treated as a finding, never as unchanged (the platform's rule against guessing at an
/// unobserved fact). The recorded tip is the one automated review read, so a branch that is anywhere else has
/// commits no reviewer has seen.
/// </para>
/// </summary>
public sealed record HumanGateFinding(HumanGateFindingKind Kind, string Detail)
{
    /// <summary>The most uncommitted paths a finding names before it counts the rest.</summary>
    public const int MaxNamedFiles = 20;

    /// <summary>Whether the pull request may open on this finding.</summary>
    public bool IsClear => Kind == HumanGateFindingKind.Unchanged;

    /// <summary>
    /// Decides what was found. <paramref name="uncommitted"/> is the blocking uncommitted paths as
    /// <c>h9k review fixed</c> counts them (modified or staged tracked files, plus untracked files under
    /// src/ or tests/), or null when git could not be asked. <paramref name="requireUnchangedTip"/> is true
    /// for a bare proceed, which approves the tip that stood at the park, and false for a merge-ready
    /// resolve, which owns a tip that moved because the gates run over it, but never over uncommitted work.
    /// </summary>
    public static HumanGateFinding Decide(
        string? recordedTip, string? observedTip, IReadOnlyList<string>? uncommitted, bool requireUnchangedTip)
    {
        if (uncommitted is null)
        {
            return new HumanGateFinding(
                HumanGateFindingKind.StatusUnobservable,
                "the worktree's git status could not be read, so whether it holds uncommitted work is unknown");
        }

        if (uncommitted.Count > 0)
        {
            return new HumanGateFinding(
                HumanGateFindingKind.UncommittedWork,
                $"the worktree holds uncommitted work, which no pull request would carry: {Named(uncommitted)}");
        }

        if (!requireUnchangedTip)
        {
            return new HumanGateFinding(HumanGateFindingKind.Unchanged, string.Empty);
        }

        if (recordedTip is null || observedTip is null)
        {
            return new HumanGateFinding(
                HumanGateFindingKind.TipUnobservable,
                recordedTip is null
                    ? "the park recorded no branch tip, so whether the branch moved cannot be told"
                    : "the branch tip could not be read, so whether the branch moved cannot be told");
        }

        return string.Equals(recordedTip, observedTip, StringComparison.Ordinal)
            ? new HumanGateFinding(HumanGateFindingKind.Unchanged, string.Empty)
            : new HumanGateFinding(
                HumanGateFindingKind.TipMoved,
                $"the branch moved from {Short(recordedTip)} to {Short(observedTip)} since the park, so commits "
                + "have landed that no review pass has read");
    }

    /// <summary>
    /// The park reason for a gate that found something and so did not open the pull request. Names what
    /// was found and the levers that answer that finding, which differ by kind because the same lever does
    /// not fit them all: <c>h9k review fixed</c> needs commits to hand back and refuses an unmoved tip,
    /// <c>h9k review proceed</c> approves a tip that is where the park left it, and
    /// <c>h9k review resolve --merge-ready</c> opens the pull request over whatever the tip is after the gates.
    /// </summary>
    public string ReparkReason(Guid taskId)
    {
        string id = taskId.ToString("D", CultureInfo.InvariantCulture);
        string fixedLever = $"h9k review fixed {id} runs the gates and a review pass over the commits";
        string resolveLever = $"h9k review resolve {id} --merge-ready --reason \"<why>\" opens the pull request over "
            + "the tip as it stands without another pass (the gates still run on it and the settlement is recorded "
            + "as Settled)";
        string levers = Kind switch
        {
            HumanGateFindingKind.UncommittedWork =>
                "Commit the work or discard it, then answer again. If you committed it, "
                + $"{fixedLever}, or {resolveLever}. If you discarded it and the branch is where review left it, "
                + $"h9k review proceed {id} approves it.",
            HumanGateFindingKind.StatusUnobservable =>
                "Check the worktree yourself (git status in it), then answer again. If it is clean and the branch is "
                + $"where review left it, h9k review proceed {id} approves it; if you committed work, {fixedLever}, "
                + $"or {resolveLever}.",
            HumanGateFindingKind.TipUnobservable =>
                "Check the branch yourself (git log in the worktree), then answer again. If you committed work, "
                + $"{fixedLever}; otherwise {resolveLever}.",
            _ =>
                $"Hand the commits to review: {fixedLever}. Or {resolveLever}.",
        };
        return $"The pull request was not opened: {Detail}. {levers}";
    }

    private static string Named(IReadOnlyList<string> files) =>
        files.Count <= MaxNamedFiles
            ? string.Join(", ", files)
            : $"{string.Join(", ", files.Take(MaxNamedFiles))} (and {files.Count - MaxNamedFiles} more)";

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
