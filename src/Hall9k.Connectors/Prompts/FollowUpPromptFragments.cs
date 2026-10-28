using System.Globalization;
using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// The two prompt fragments a pull-request follow-up shares between the daemon's headless laps
/// (<c>AgentPromptBuilder</c>) and the task anchor <c>h9k task drive</c> writes for a task whose
/// pull request is already open: which review comments a session may read, and how its fixes land
/// on the branch. They live here, beside <see cref="WorkPromptBuilder"/>, because the CLI cannot
/// reference <c>Hall9k.Daemon</c>; both sides call this one copy, and both call the same
/// shared-history and owner-voice rules <see cref="WorkPromptBuilder"/> owns.
/// </summary>
public static class FollowUpPromptFragments
{
    /// <summary>
    /// How many comment ids the read-scope section names before it stops. The membership gate
    /// already caps a scope at this many, so this only matters for a scope recorded under a larger
    /// cap; a comment not named is a comment the session may not read, so cutting the list short
    /// only ever reads less.
    /// </summary>
    public const int MaxReadableCommentIds = 500;

    /// <summary>
    /// How much owner-approved comment text, summed across every thread, rides into a prompt whole
    /// before the rest are replaced by a pointer. The review-feedback prompt bounds its own finding
    /// bodies by the same ceiling.
    /// </summary>
    public const int MaxCopiedCommentTotalLength = 200_000;

    /// <summary>
    /// Where the narrative commit style's fixup-fold rebases from, for a branch whose
    /// <c>origin/&lt;base&gt;</c> is not safe to name there: a stacked child's parent branch, which
    /// a force-push moves out from under the fold, collapsing its merge base below this branch's
    /// own commits so the fold rewrites the parent's already-reviewed work as this branch's
    /// authored history — and the tree-identity check cannot always catch that, since replaying
    /// the parent's own commits can leave the tree where it was (independent pre-PR review,
    /// cycle 2, adversarial lens).
    /// <para>
    /// <paramref name="Argument"/> is what follows <c>--autosquash</c>: a literal commit wherever
    /// the platform observed one, or a described placeholder where only the session can know it —
    /// a rebase that has just moved this branch onto a new base leaves the recorded fork point off
    /// this branch's line entirely, so the fold's boundary is the commit the session replayed onto,
    /// which it records for itself. <paramref name="Reason"/> is the caller's own explanation,
    /// appended verbatim (indentation included) under the command, because which boundary is right
    /// and why differs per prompt.
    /// </para>
    /// </summary>
    public sealed record FoldBoundary(string Argument, IReadOnlyList<string> Reason);

    private static string Fragment(string file, string name, params (string Key, string Value)[] values)
    {
        string raw = PromptTemplates.Load(file, name);
        if (raw.Contains('\n'))
        {
            throw new InvalidOperationException(
                $"Fragment '{name}' in {file} spans multiple lines; append it with AppendFragment "
                + "(line by line, each ended with '\\n') instead of Fragment paired with a single "
                + "AppendLine, which is meant for one authored line.");
        }

        return values.Length == 0
            ? raw
            : PromptTemplates.Load(file, name, values.ToDictionary(value => value.Key, value => value.Value));
    }

    private static void AppendFragment(
        StringBuilder prompt, string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.AppendTemplate(
            prompt, file, name, values.Length == 0 ? null : values.ToDictionary(value => value.Key, value => value.Value));

    private static string[] FragmentLines(string file, string name, params (string Key, string Value)[] values) =>
        PromptTemplates.Load(file, name, values.ToDictionary(value => value.Key, value => value.Value)).Split('\n');

    private static string OneLine(string text) => RelayedText.OneLine(text).Trim();

    /// <summary>
    /// Which unresolved review comments this lap's session may read, and which it must leave alone
    /// (task: a changes-requested review or an unresolved review thread from someone outside the
    /// project's trust-chain roster never reaches a build lap). The daemon decided it from the roster
    /// at dispatch and recorded it on the reopen; this only renders it. No recorded scope reads as
    /// nothing readable, never as everything: a reopen from before the gate, or a manual one on a task
    /// closeout never dispatched, must not be a way round it.
    /// </summary>
    public static void AppendReviewReadScope(StringBuilder prompt, TaskDetails task, bool inReviewLap = false) =>
        AppendReviewReadScope(prompt, task.ReviewReadScope, inReviewLap);

    /// <summary>
    /// <see cref="AppendReviewReadScope(StringBuilder, TaskDetails, bool)"/> over a scope in hand, for the
    /// read-only closeout triage, which is handed the scope closeout just decided rather than one a
    /// reopen recorded.
    /// </summary>
    public static void AppendReviewReadScope(StringBuilder prompt, ReviewReadScope? scope, bool inReviewLap = false)
    {
        const string file = $"{WorkPromptBuilder.TemplateDirectory}/review-read-scope.md";
        prompt.AppendLine(Fragment(file, "heading"));
        prompt.AppendLine();
        if (scope is { Unrestricted: true })
        {
            AppendFragment(prompt, file, "unrestricted");
            prompt.AppendLine();
            return;
        }

        AppendFragment(prompt, file, "intro");
        prompt.AppendLine();
        IReadOnlyList<ReadableReviewThread> threads = scope?.Threads ?? [];
        if (threads.Count == 0)
        {
            AppendFragment(prompt, file, inReviewLap ? "no-other-readable-threads" : "no-readable-threads");
        }
        else
        {
            prompt.AppendLine(Fragment(file, "readable-heading"));
            prompt.AppendLine();
            int shown = 0;
            int copiedLength = 0;
            bool omitted = false;
            bool copiesQuoted = false;
            foreach (ReadableReviewThread thread in threads)
            {
                string origin = thread.OpenedBy == FeedbackAdmission.Bot
                    ? Fragment(file, "origin-bot")
                    : thread.OpenedBy == FeedbackAdmission.OwnerApproved
                        ? Fragment(file, "origin-approved")
                        : Fragment(file, "origin-member");
                IEnumerable<string> ids = thread.CommentIds.Take(MaxReadableCommentIds - shown);
                string named = string.Join(", ", ids.Select(id => $"`{OneLine(id)}`"));
                shown += ids.Count();
                omitted |= shown >= MaxReadableCommentIds && thread.CommentIds.Count > ids.Count();
                IReadOnlyList<ThreadCommentCopy> copies = thread.ApprovedCopies ?? [];
                string threadFragment = named.Length > 0
                    ? "readable-thread"
                    : copies.Count > 0
                        ? "thread-copies-only"
                        : "thread-without-comments";
                prompt.AppendLine(Fragment(
                    file, threadFragment,
                    ("ThreadId", OneLine(thread.ThreadId)),
                    ("Opener", OneLine(thread.Opener)),
                    ("Origin", origin),
                    ("CommentIds", named)));
                foreach (ThreadCommentCopy copy in copies)
                {
                    copiesQuoted = true;
                    prompt.AppendLine();
                    if (copiedLength >= MaxCopiedCommentTotalLength)
                    {
                        prompt.AppendLine(Fragment(
                            file, "approved-comment-omitted",
                            ("CommentId", OneLine(copy.Id)), ("Opener", OneLine(thread.Opener))));
                        continue;
                    }

                    // Whole and never cut: the gate keeps no copy over the bound this prompt carries, so a
                    // truncation notice pointing back at the live comment cannot arise here.
                    string fenced = RelayedText.Fenced(RelayedText.Printable(copy.Body));
                    prompt.AppendLine(Fragment(
                        file, "approved-comment", ("CommentId", OneLine(copy.Id)), ("Opener", OneLine(thread.Opener))));
                    prompt.AppendLine(fenced);
                    copiedLength += fenced.Length;
                }

                if (copies.Count > 0)
                {
                    prompt.AppendLine();
                }
            }

            prompt.AppendLine();
            if (copiesQuoted)
            {
                AppendFragment(prompt, file, "approved-copies-note");
                prompt.AppendLine();
            }

            if (omitted)
            {
                AppendFragment(
                    prompt, file, "more-comments-omitted",
                    ("Shown", MaxReadableCommentIds.ToString(CultureInfo.InvariantCulture)));
                prompt.AppendLine();
            }

            AppendFragment(prompt, file, "how-to-read");
            prompt.AppendLine();
            AppendFragment(prompt, file, "read-command");
            prompt.AppendLine();
            AppendFragment(prompt, file, "never-wider");
        }

        prompt.AppendLine();
        if (scope is { WithheldThreadIds.Count: > 0 })
        {
            AppendFragment(
                prompt, file, "withheld",
                ("WithheldThreadIds", string.Join(", ", scope.WithheldThreadIds.Select(id => $"`{OneLine(id)}`"))));
            prompt.AppendLine();
        }

        AppendFragment(prompt, file, "never-file-stranger-text");
        prompt.AppendLine();
    }

    /// <summary>
    /// How a follow-up's fixes land on the PR branch (Decisions Log #26). Narrative
    /// enforces the AGENTS.md authored-history rule: fixups mapped by file ownership,
    /// autosquash onto the base, and a tree-identity check so the verification-gate
    /// results honestly describe the rebased tree the platform will force-push. Append
    /// keeps the historic stack-on-top behavior. Both end the same way: the agent never
    /// pushes; the platform does, with --force-with-lease for follow-up runs.
    /// </summary>
    /// <param name="fold">
    /// Where the fold rebases from when <paramref name="baseBranch"/> is a branch this one is
    /// stacked on rather than the project's own — see <see cref="FoldBoundary"/>. Nothing in this
    /// prompt moves the branch, so the recorded fork point is still this branch's own boundary
    /// here, unlike in a rebase prompt. Null for every ordinary run.
    /// </param>
    /// <param name="voiceSkill">
    /// The owner's own voice skill, when they named one: a commit message is authored history read
    /// under the owner's login exactly as a pull request body is, so this seam names the skill too
    /// (#193). Rendered on both style arms, since both end in commit messages
    /// somebody writes.
    /// </param>
    public static void AppendCommitStyleRules(
        StringBuilder prompt, CommitStyle commitStyle, string baseBranch, FoldBoundary? fold = null,
        VoiceSkillName? voiceSkill = null)
    {
        const string file = $"{WorkPromptBuilder.TemplateDirectory}/commit-style.md";
        WorkPromptBuilder.AppendSharedRepositoryHistorySafetyRule(prompt);
        if (commitStyle == CommitStyle.Append)
        {
            AppendFragment(prompt, file, "append-style");
            WorkPromptBuilder.AppendOwnerVoiceRule(prompt, string.Empty, voiceSkill, WorkPromptBuilder.CodeReviewVoiceContext);
            return;
        }

        AppendFragment(prompt, file, "narrative-lead");
        if (fold is not null)
        {
            // That skill folds against `origin/<base>`, which is the one thing a stacked child may
            // not do — the pointer is qualified rather than left to contradict the boundary named
            // below (independent pre-PR review, cycle 2, adversarial lens).
            AppendFragment(prompt, file, "stacked-skill-caveat", ("BaseBranch", baseBranch));
        }

        AppendFragment(prompt, file, "map-and-land-fixups");
        prompt.AppendLine(Fragment(file, "fold-command", ("Argument", fold?.Argument ?? $"origin/{baseBranch}")));
        foreach (string line in fold?.Reason ?? [])
        {
            prompt.AppendLine(line);
        }

        AppendFragment(prompt, file, "tree-identity-and-push");
        WorkPromptBuilder.AppendOwnerVoiceRule(prompt, string.Empty, voiceSkill, WorkPromptBuilder.CodeReviewVoiceContext);
    }

    /// <summary>
    /// The fold boundary for a follow-up that resumes a stacked child's branch without moving it —
    /// a review-feedback or failing-checks lap. The run's own recorded fork point is still this
    /// branch's boundary there, precisely because nothing in those prompts rebases the branch onto
    /// anything (a rebase prompt's own fold is a different commit; see the daemon's rebase prompt).
    /// Null for every ordinary run and for a stacked run with no recorded fork point, both of which
    /// keep the <c>origin/&lt;base&gt;</c> wording exactly as it was.
    /// </summary>
    public static FoldBoundary? ResumedStackedFold(
        ProjectDetails project, string effectiveBaseBranch, string? baseCommit)
    {
        const string file = $"{WorkPromptBuilder.TemplateDirectory}/commit-style.md";
        return WorkPromptBuilder.StackedForkPoint(project, effectiveBaseBranch, baseCommit) is { } forkPoint
            ? new FoldBoundary(
                forkPoint,
                FragmentLines(file, "resumed-stacked-fold-reason", ("EffectiveBaseBranch", effectiveBaseBranch)))
            : null;
    }
}
