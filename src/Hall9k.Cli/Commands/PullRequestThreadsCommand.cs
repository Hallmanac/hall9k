using System.ComponentModel;
using System.Globalization;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Every inline review thread on a pull request, read-only, for a review lap that cannot get to
/// them any other way. <c>gh pr view</c> and <c>gh pr diff</c> do not return inline review
/// comments, and the lap's push guard denies <c>gh api</c> wholesale, which is right as a write
/// gate; this command is the read, run as h9k's own child process under the project's own GitHub
/// account the way <c>h9k pr review</c>'s reads are (arx-platform#2201, hall9k #336).
/// <para>
/// It writes nothing to GitHub and appends nothing to the task store: it takes an
/// <see cref="IQuerySession"/>, so there is no session here that could append, and it does not
/// copy <c>h9k pr review</c>'s node bootstrap.
/// </para>
/// <para>
/// It reads through <see cref="GitHubReviewThreads"/>, the same capped query the scoped lap's
/// packet uses, so it cannot show what that packet could not; the output says so wherever a cap
/// was hit rather than presenting a capped read as the whole conversation.
/// </para>
/// </summary>
public sealed class PullRequestThreadsCommand : Hall9kAsyncCommand<PullRequestThreadsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PULL-REQUEST>")]
        [Description(
            "The pull request whose review threads to read: the number (42 or #42), the owner/repo#42 "
            + "shorthand, or the pull request URL on github.com. Read through the gh CLI from the "
            + "project's repository under the project's own GitHub account, the same way h9k pr review "
            + "reads it")]
        public string PullRequest { get; init; } = string.Empty;

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Project whose repository the pull request belongs to: its name, an unambiguous fragment, or "
            + "its full id (h9k project list shows them all). Optional when exactly one project is "
            + "registered, which is the ordinary single-project install")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();
        return await RunAsync(
            session, settings, new ProjectScopedGitHubRunner(store).Runner, Console.Out, cancellationToken);
    }

    /// <summary>
    /// The whole command body, with its two outside seams handed in: <paramref name="processRunner"/>
    /// is how it reaches <c>gh</c>, and <paramref name="output"/> is where the threads are written.
    /// </summary>
    internal static async Task<int> RunAsync(
        IQuerySession session,
        Settings settings,
        ProcessRunner processRunner,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ProjectDetails project = await PullRequestReviewCommand.ResolveProjectAsync(
            session, settings.Project, cancellationToken);

        PullRequestSurface pullRequest = await new GitHubPullRequestSurface(processRunner).ReadAsync(
            settings.PullRequest, project.RepositoryPath, cancellationToken);
        ReviewConversation conversation = await new GitHubReviewThreads(processRunner).ReadAsync(
            pullRequest.Repository, pullRequest.Number, project.RepositoryPath, cancellationToken);

        await PrintAsync(pullRequest.Repository, pullRequest.Number, conversation, output, cancellationToken);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Writes <see cref="Lines"/> to <paramref name="output"/>, the writer and never AnsiConsole:
    /// Spectre rewraps at eighty columns whenever stdout is not a TTY, which it never is for an
    /// agent's Bash call, and it parses square brackets as markup. A comment rewrapped or parsed
    /// is no longer the comment.
    /// </summary>
    internal static async Task PrintAsync(
        string repository,
        int number,
        ReviewConversation conversation,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        foreach (string line in Lines(repository, number, conversation))
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    /// <summary>
    /// The printed threads, line by line, with no gh and no database in it. Comment bodies pass
    /// through <see cref="ExternalText.ForTerminal"/>, keep their own layout, and are fenced
    /// (<see cref="RelayedText.Fenced"/>) so nothing marks the end of one but the command's own
    /// closing fence, which the body cannot close early; every one-line field (a login, a path) is
    /// folded to one line. Either way a value free to emit a newline cannot print a row of its own
    /// choosing.
    /// </summary>
    internal static IReadOnlyList<string> Lines(string repository, int number, ReviewConversation conversation)
    {
        string reference = $"{ExternalText.OneLine(repository)}#{number.ToString(CultureInfo.InvariantCulture)}";
        List<string> lines = [];

        if (conversation.Threads.Count == 0 && !conversation.ThreadsTruncated)
        {
            lines.Add($"{reference} has no review threads.");
            return lines;
        }

        lines.Add($"Review threads on {reference}: {conversation.Threads.Count.ToString(CultureInfo.InvariantCulture)} read.");
        if (conversation.ThreadsTruncated)
        {
            lines.Add(
                $"NOT COMPLETE: this pull request carries more review threads than the {GitHubReviewThreads.ThreadPageSize} "
                + "one read returns, and the rest are not shown.");
        }

        for (int index = 0; index < conversation.Threads.Count; index++)
        {
            ReviewThread thread = conversation.Threads[index];
            lines.Add(string.Empty);
            lines.Add(
                $"Thread {(index + 1).ToString(CultureInfo.InvariantCulture)}: {ExternalText.OneLine(thread.Location())}");
            lines.Add($"Status: {(thread.IsResolved ? "resolved" : "unresolved")}");
            lines.Add($"Opened by: {Author(thread.StartedByLogin)}");

            if (thread.Comments.Count == 0)
            {
                lines.Add("No comments were read on this thread.");
            }

            for (int commentIndex = 0; commentIndex < thread.Comments.Count; commentIndex++)
            {
                ReviewThreadComment comment = thread.Comments[commentIndex];
                lines.Add(string.Empty);
                lines.Add(
                    $"Comment {(commentIndex + 1).ToString(CultureInfo.InvariantCulture)} by {Author(comment.AuthorLogin)}:");
                lines.Add(RelayedText.Fenced(ExternalText.ForTerminal(comment.Body)));
            }

            if (thread.UnreadCommentCount > 0)
            {
                lines.Add(string.Empty);
                lines.Add(
                    $"NOT COMPLETE: {thread.UnreadCommentCount.ToString(CultureInfo.InvariantCulture)} of this thread's newest "
                    + $"comments are not shown; this read returns only the first {GitHubReviewThreads.CommentPageSize}.");
            }
        }

        return lines;
    }

    /// <summary>
    /// A login as the provider reported it, or a plain statement that it reported none: a deleted
    /// account or a malformed node is never attributed to anyone.
    /// </summary>
    private static string Author(string? login) => login switch
    {
        { Length: > 0 } reported => ExternalText.OneLine(reported),
        _ => "no reported author",
    };
}
