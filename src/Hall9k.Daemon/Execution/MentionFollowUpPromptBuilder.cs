using System.Text;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The bounded follow-up lap's own prompt (idea 2f079bcd, auto-pr-review's second trigger, decision
/// 2): dispatched automatically by <see cref="RunLauncher.LaunchPrReviewMentionFollowUpAsync"/> when
/// a fresh GitHub comment mentions the install's login on a pull request this install already
/// reviewed. Deliberately its own small builder rather than a mode of
/// <see cref="AgentPromptBuilder.BuildPrReviewLens"/>: that method's whole prompt is the shared
/// independent-review apparatus (findings, severity, both lenses) built around re-reviewing a diff,
/// and this session reviews nothing — it reads one comment against a review that already happened
/// and drafts one answer. Reusing that machinery for a single question would either drag its whole
/// findings/severity contract along for no reason or fork it with a mode flag threaded through
/// every internal it touches, for a prompt this much narrower.
/// <para>
/// The session's own FINAL answer, not a file it writes, becomes the addendum verbatim
/// (<c>PrReviewEngine.DriveMentionFollowUpAsync</c> reads it off the completed session's own
/// result the same way <c>RecordAdversarialResultAsync</c> already does for the ordinary review's
/// primary session) — the template's own closing instructions say so, and are the one thing here
/// that must never drift from that reader.
/// </para>
/// </summary>
public static class MentionFollowUpPromptBuilder
{
    /// <summary>The package name this builder's own prose ships under in <c>.claude/templates</c> (and the canonical/release-payload equivalents).</summary>
    public const string TemplateDirectory = "mention-followup-prompt-builder";

    public static string Build(
        string repository, int number, string worktreePath, string baseBranch,
        PullRequestMentionComment comment, string? priorReport)
    {
        const string file = $"{TemplateDirectory}/build.md";
        StringBuilder prompt = new();

        prompt.AppendLine(PromptTemplates.Load(file, "title", Params(("RepoAndNumber", $"{repository}#{number}"))));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            file, "intro", Params(("CommentAuthor", OneLine(comment.AuthorLogin)))));
        prompt.AppendLine();

        prompt.AppendLine(PromptTemplates.Load(file, "the-comment-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            file, "the-comment-body",
            Params(("CommentAuthor", OneLine(comment.AuthorLogin)), ("CommentTime", comment.CreatedAt.ToString("u")))));
        prompt.AppendLine();
        prompt.AppendLine(Block(comment.Body));
        if (comment.Url.IsNotBlank())
        {
            prompt.AppendLine();
            prompt.AppendLine(comment.Url);
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "prior-report-heading"));
        prompt.AppendLine();
        if (priorReport.IsNotBlank())
        {
            prompt.AppendLine(PromptTemplates.Load(file, "prior-report-present"));
            prompt.AppendLine();
            prompt.AppendLine(Block(priorReport));
        }
        else
        {
            prompt.AppendLine(PromptTemplates.Load(file, "prior-report-absent"));
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "working-arrangement-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            file, "working-arrangement-body", Params(("WorktreePath", worktreePath), ("BaseBranch", baseBranch))));
        prompt.AppendLine();

        prompt.AppendLine(PromptTemplates.Load(file, "what-to-produce-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "what-to-produce-body"));
        prompt.AppendLine();

        prompt.AppendLine(PromptTemplates.Load(file, "rules-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "rules-body"));

        return prompt.ToString();
    }

    /// <summary>
    /// The addendum a fresh mint's own primary (adversarial) session gets appended to its
    /// ordinary review prompt when the mint itself came from a mention (idea 2f079bcd, decision
    /// 2): unlike <see cref="Build"/>, this session's own final answer is its ordinary review
    /// verdict, so the "You were asked" content is written to its own file
    /// (<c>mention-answer.md</c>) instead, which <c>PrReviewEngine.ComposeReportAndParkAsync</c>
    /// reads and appends to the findings report — the same shape a mention that attaches to an
    /// already-reviewed pull request gets from <see cref="Build"/>, just delivered alongside a
    /// full review rather than in place of one.
    /// </summary>
    public static string BuildMintAddendum(
        string commentAuthorLogin, DateTimeOffset commentCreatedAt, string commentBody, string? commentUrl,
        string runDirectory)
    {
        const string file = $"{TemplateDirectory}/mint-addendum.md";
        StringBuilder prompt = new();

        prompt.AppendLine(PromptTemplates.Load(file, "heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "intro"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            file, "the-comment-body",
            Params(("CommentAuthor", OneLine(commentAuthorLogin)), ("CommentTime", commentCreatedAt.ToString("u")))));
        prompt.AppendLine();
        prompt.AppendLine(Block(commentBody));
        if (commentUrl.IsNotBlank())
        {
            prompt.AppendLine();
            prompt.AppendLine(commentUrl);
        }

        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(file, "what-to-produce-heading"));
        prompt.AppendLine();
        prompt.AppendLine(PromptTemplates.Load(
            file, "what-to-produce-body", Params(("MentionAnswerPath", Path.Combine(runDirectory, "mention-answer.md")))));

        return prompt.ToString();
    }

    private static Dictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);

    private static string OneLine(string text) => RelayedText.OneLine(text).Trim();

    private static string Block(string text) => RelayedText.Printable(text);
}
