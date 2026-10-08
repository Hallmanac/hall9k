using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k pr threads</c> prints, over a fixture <see cref="ReviewConversation"/> so no gh and
/// no git runs. The reader's only real caller is an agent's Bash call, where stdout is never a
/// terminal, so the long-comment case is checked on the exact write path the command uses rather
/// than on the line composer alone.
/// </summary>
public sealed class PullRequestThreadsOutputTests
{
    private const string Repository = "acme/web";
    private const int Number = 42;

    // Longer than eighty columns, with line breaks and square brackets, so a rewrap or a markup
    // parse on the way out would both show.
    private static readonly string LongComment =
        "This line is deliberately far longer than eighty columns so that a writer which rewraps at the width of a terminal would break it in the middle of this sentence.\n"
        + "Second line: see [docs/cli.md] and the [bold]literal[/] brackets in it.\n"
        + "\n"
        + "Third paragraph, after a blank line.";

    private static ReviewThread Thread(
        string id,
        bool resolved,
        string? startedBy,
        string? path,
        int? line,
        int commentCount,
        params ReviewThreadComment[] comments) =>
        new(id, resolved, startedBy, path, line, commentCount, comments);

    private static ReviewConversation Conversation(bool threadsTruncated, params ReviewThread[] threads) =>
        new(
            IsOpen: true,
            IsMerged: false,
            IsClosed: false,
            HeadSha: null,
            CommitCount: null,
            Threads: threads,
            OutstandingReviewerLogins: [],
            ThreadsTruncated: threadsTruncated,
            LatestReviewByLogin: new Dictionary<string, SubmittedReview>(),
            ReviewsTruncated: false);

    private static string Render(ReviewConversation conversation) =>
        string.Join("\n", PullRequestThreadsCommand.Lines(Repository, Number, conversation));

    [Fact]
    public void A_resolved_and_an_unresolved_thread_each_print_location_status_opener_and_every_comment()
    {
        ReviewConversation conversation = Conversation(
            threadsTruncated: false,
            Thread("T1", resolved: true, "copilot", "src/A.cs", 12, 2,
                new ReviewThreadComment("copilot", "Null check missing.", null),
                new ReviewThreadComment("author", "Fixed in the next commit.", null)),
            Thread("T2", resolved: false, "reviewer", "src/B.cs", null, 1,
                new ReviewThreadComment("reviewer", "Why is this public?", null)));

        string output = Render(conversation);

        output.Should().Contain("src/A.cs:12").And.Contain("Status: resolved").And.Contain("Opened by: copilot");
        output.Should().Contain("Comment 1 by copilot:\nNull check missing.");
        output.Should().Contain("Comment 2 by author:\nFixed in the next commit.");
        output.Should().Contain("Thread 2: src/B.cs\n").And.Contain("Status: unresolved").And.Contain("Opened by: reviewer");
        output.Should().Contain("Comment 1 by reviewer:\nWhy is this public?");
    }

    [Fact]
    public void A_thread_and_a_comment_with_no_reported_author_are_labelled_rather_than_attributed()
    {
        ReviewConversation conversation = Conversation(
            threadsTruncated: false,
            Thread("T1", resolved: false, startedBy: null, "src/A.cs", 3, 2,
                new ReviewThreadComment(null, "Left by an account that was since deleted.", null),
                new ReviewThreadComment("author", "Noted.", null)));

        string output = Render(conversation);

        output.Should().Contain("Opened by: no reported author");
        output.Should().Contain("Comment 1 by no reported author:\nLeft by an account that was since deleted.");
        output.Should().Contain("Comment 2 by author:");
    }

    [Fact]
    public void A_capped_thread_page_says_the_rest_are_not_shown()
    {
        ReviewConversation conversation = Conversation(
            threadsTruncated: true,
            Thread("T1", resolved: false, "reviewer", "src/A.cs", 1, 1,
                new ReviewThreadComment("reviewer", "One.", null)));

        Render(conversation).Should().Contain(
            "more review threads than the 100 one read returns, and the rest are not shown");
    }

    [Fact]
    public void A_thread_with_unread_comments_says_how_many_of_its_newest_are_not_shown_on_that_thread()
    {
        ReviewConversation conversation = Conversation(
            threadsTruncated: false,
            Thread("T1", resolved: false, "reviewer", "src/A.cs", 1, commentCount: 103,
                new ReviewThreadComment("reviewer", "First.", null)),
            Thread("T2", resolved: false, "reviewer", "src/B.cs", 2, 1,
                new ReviewThreadComment("reviewer", "Only one.", null)));

        string output = Render(conversation);

        // 103 total with one read is 102 unread; the notice sits under thread 1 and not thread 2.
        output.Should().Contain("102 of this thread's newest comments are not shown");
        output.IndexOf("102 of this thread's newest", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("Thread 2:", StringComparison.Ordinal));
        output.Should().NotContain("more review threads than");
    }

    [Fact]
    public void A_pull_request_with_no_threads_says_it_has_none()
    {
        Render(Conversation(threadsTruncated: false)).Should().Be("acme/web#42 has no review threads.");
    }

    [Fact]
    public async Task A_long_comment_with_line_breaks_and_brackets_is_written_unchanged_to_a_non_terminal_writer()
    {
        ReviewConversation conversation = Conversation(
            threadsTruncated: false,
            Thread("T1", resolved: false, "reviewer", "src/A.cs", 1, 1,
                new ReviewThreadComment("reviewer", LongComment, null)));
        StringWriter output = new();

        await PullRequestThreadsCommand.PrintAsync(Repository, Number, conversation, output, CancellationToken.None);

        string written = output.ToString().ReplaceLineEndings("\n");
        written.Should().Contain(LongComment);
        written.Should().Contain("[bold]literal[/]", "square brackets print as themselves, never as markup");
    }

    [Fact]
    public void Terminal_control_characters_in_a_comment_are_dropped_and_in_a_login_folded_to_one_line()
    {
        string escape = ((char)0x1B).ToString();
        ReviewConversation conversation = Conversation(
            threadsTruncated: false,
            Thread("T1", resolved: false, "evil\nOpened by: someone-else", "src/A.cs", 1, 1,
                new ReviewThreadComment("reviewer", $"before{escape}[2Jafter", null)));

        string output = Render(conversation);

        output.Should().NotContain(escape);
        output.Should().NotContain("\nOpened by: someone-else");
    }
}
