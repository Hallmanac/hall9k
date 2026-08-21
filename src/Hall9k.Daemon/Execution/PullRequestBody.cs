using System.Text;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// What the daemon writes into a pull request. Its own type because it is the one artifact of a
/// run that outlives Hall9k: reviewers read it in GitHub, long after the run directory is gone.
/// </summary>
internal static class PullRequestBody
{
    /// <summary>
    /// What the pull request is called. It is the objective, and the objective of an adopted task
    /// is an issue title someone else wrote (PLAN.md §3.1a), so it needs the same defusal the body
    /// gives relayed text — more urgently, in fact. A title looks like the safest place for it and
    /// is the most dangerous: GitHub's default squash-merge commit message <em>is</em> the pull
    /// request title, so an adopted issue called "Fix login, resolves #500" would close issue 500
    /// the moment this merged, having never appeared in the body the defusal guards.
    /// <para>
    /// It is folded to one printable line first. A title is a commit subject by the time it
    /// matters, and a newline or an escape sequence in one lands in the repository's history and
    /// in every terminal that later runs git log. Which characters those are is
    /// <see cref="RelayedText"/>'s rule, the same one the CLI asks at the other sink: the daemon
    /// cannot reference the CLI (AGENTS.md, reference graph), and a second hand-written list of
    /// bidirectional controls is a list that drifts out of agreement with the first.
    /// </para>
    /// </summary>
    public static string Title(string objective) => OneLine(objective);

    /// <summary>
    /// Relayed text that belongs on one line — the objective, a checklist item — defused twice
    /// over: the closing keywords lose their power over the issue tracker, and the layout
    /// characters become spaces while everything the terminal or the renderer would obey rather
    /// than show is dropped. Words on separate lines do not run together, and a criterion cannot
    /// break out of the checklist item it is inside.
    /// </summary>
    private static string OneLine(string text) =>
        WithoutClosingKeywords(RelayedText.OneLine(text).Trim());

    /// <summary>
    /// Relayed text that is a block of prose and keeps its shape: the same defusal, but the line
    /// breaks and tabs survive, because they are this text's paragraphs and its lists.
    /// </summary>
    private static string Block(string text) => WithoutClosingKeywords(RelayedText.Printable(text));

    public static string Build(RunDetails run, TaskDetails task, string? agentSummary, Uri? sourceUrl)
    {
        StringBuilder body = new();
        body.AppendLine(OneLine(task.Objective));
        body.AppendLine();
        body.AppendLine("## Acceptance criteria");
        foreach (string criterion in task.AcceptanceCriteria)
        {
            body.AppendLine($"- [ ] {OneLine(criterion)}");
        }

        if (SourceMention(task.ExternalReference, sourceUrl) is { } mention)
        {
            body.AppendLine();
            body.AppendLine(mention);
        }

        if (agentSummary.IsNotBlank())
        {
            body.AppendLine();
            body.AppendLine("## Agent summary");
            body.AppendLine(Block(agentSummary));
        }

        long totalTokens = run.InputTokens + run.CacheReadInputTokens + run.CacheCreationInputTokens + run.OutputTokens;
        body.AppendLine();
        body.AppendLine("---");
        body.AppendLine($"Hall9k run `{run.Id}` · {totalTokens} tokens");
        return body.ToString();
    }

    /// <summary>
    /// The line that links the work back to the item it belongs to: a plain mention of that
    /// item's URL, which GitHub turns into a cross-reference on the issue's own timeline.
    /// <para>
    /// Deliberately a mention and not a closing keyword. "Closes #42" would make merging this
    /// pull request change the issue's state, and Hall9k does not move an external item's status:
    /// which transitions should follow a merge is a policy question (SLICE-1 S1-11, Decisions Log
    /// #64, where Jira gets a comment at merge and never a transition, for the same reason). A
    /// cross-reference gives a reviewer the round trip without the platform deciding anything.
    /// </para>
    /// <para>
    /// The wording says what is true of both ways a task acquires a reference, and says no more
    /// than that. "Adopted from" was true while adoption was the only route (§3.1a), and is a
    /// false provenance claim for a card that exists <em>because</em> of the task
    /// (h9k task push-to-jira). The projection carries one reference field either way, so the
    /// body names the link rather than guessing which direction it was made in.
    /// </para>
    /// <para>
    /// The URL is resolved by the caller through the connection-aware resolver seam rather than
    /// formatted here, because placing a Jira reference needs the site its connection recorded
    /// and this class has no session to read one from. A reference no registered source can place
    /// falls back to its canonical form, which is still the honest identifier.
    /// </para>
    /// </summary>
    private static string? SourceMention(string? externalReference, Uri? sourceUrl) =>
        externalReference.IsBlank()
            ? null
            : $"Work item: {sourceUrl?.ToString() ?? ExternalReference.Parse(externalReference).ToString()}";

    /// <summary>
    /// A closing keyword rendered so GitHub reads it as words rather than as an instruction. The
    /// mention above is the only thing in this body allowed to reach the issue tracker, and only
    /// as a cross-reference; nothing Hall9k merely relays may move an item's state.
    /// <para>
    /// The rule itself is <see cref="RelayedText.WithoutClosingKeywords"/>, beside the seam that
    /// lets such text in, because the objective now arrives here already defused: the CLI applies
    /// the same rule when it seeds an objective from an issue title, so the keyword is dead before
    /// an agent ever reads it and cannot be echoed live into a commit subject. Two surfaces
    /// answering one question from two hand-written regexes are two answers that drift, and the
    /// shared one is what makes the second pass here idempotent — a reference already inside a
    /// code span is left alone rather than wrapped twice.
    /// </para>
    /// <para>
    /// This is only half of what relayed text needs, which is why nothing calls it directly: the
    /// body is not merely read on github.com. A repository set to squash with "title and
    /// description" puts the whole of it into the commit message, so an escape sequence or a
    /// bidirectional override in it lands in the repository's history and in every terminal that
    /// later runs git log — the exact threat <see cref="Title"/> was hardened against, arriving
    /// through the paragraph underneath it. So every relayed segment goes through
    /// <see cref="OneLine"/> or <see cref="Block"/>, which pair this with the printable rule.
    /// </para>
    /// </summary>
    private static string WithoutClosingKeywords(string text) => RelayedText.WithoutClosingKeywords(text);
}
