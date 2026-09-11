using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// Byte-for-byte (modulo line-ending normalization, since CI runs both ubuntu and windows)
/// proof that <see cref="ReviewLapPromptBuilder.Build"/>'s output has not moved, across this
/// task's move of its prose into <c>.claude/templates/review-lap-prompt-builder</c>. The four
/// fixtures under <c>Fixtures/PromptGoldens/ReviewLapPromptBuilder</c> were captured from this
/// branch's own first commit, before any of that file's prose moved into a template, and are
/// never hand-edited afterward — only regenerated (<c>UPDATE_GOLDENS=1</c>) from a builder whose
/// output is believed correct, the same discipline a snapshot-test library would give this
/// project if one were already a dependency.
/// <para>
/// Four scenarios, chosen to walk every major branch <see cref="ReviewLapPromptBuilder.Build"/>
/// takes: an ordinary lap with everything populated, an ordinary lap with everything absent (no
/// task, no findings report, no author run, no worktree, no file list, no CI rollup), a scoped
/// <c>--since-my-review</c> lap with a truncated thread page and a re-request, and a scoped lap
/// with nothing in either half of its packet.
/// </para>
/// </summary>
// Reads Environment.GetEnvironmentVariable("UPDATE_GOLDENS") to opt into fixture regeneration —
// not a HALL9K_HOME-derived path, but HomeEnvironmentIsolationTests' own scan flags every use of
// that method by name regardless of which variable, and errs toward requiring this attribute
// rather than trying to tell the two apart from source text alone.
[Collection("Hall9kHome")]
public sealed class ReviewLapPromptBuilderGoldenTests
{
    // Fixed, not DomainId.New(): the golden fixtures must be byte-stable across a capture run
    // and every later comparison run, and this id is printed verbatim into the prompt (the
    // log-interaction and h9k pr approve/request-changes lines).
    private static readonly Guid TaskId = Guid.Parse("01a09109-7c9b-778c-bc16-f0a23459bbd6");

    [Fact]
    public void An_ordinary_lap_with_everything_populated_matches_its_golden() =>
        AssertMatchesGolden("full", ReviewLapPromptBuilder.Build(FullBriefing()));

    [Fact]
    public void An_ordinary_lap_with_everything_absent_matches_its_golden() =>
        AssertMatchesGolden("minimal", ReviewLapPromptBuilder.Build(MinimalBriefing()));

    [Fact]
    public void A_scoped_lap_with_a_truncated_page_and_a_re_request_matches_its_golden() =>
        AssertMatchesGolden("since-my-review", ReviewLapPromptBuilder.Build(SinceMyReviewBriefing()));

    [Fact]
    public void A_scoped_lap_with_nothing_in_either_half_matches_its_golden() =>
        AssertMatchesGolden("since-my-review-minimal", ReviewLapPromptBuilder.Build(SinceMyReviewMinimalBriefing()));

    private static void AssertMatchesGolden(string name, string actual)
    {
        string path = GoldenPath(name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).Should().BeTrue($"golden fixture {path} must be captured before this test can run");
        string expected = File.ReadAllText(path);
        Normalize(actual).Should().Be(Normalize(expected));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string GoldenPath(string name) =>
        Path.Combine(RepositoryRoot(), "tests", "Hall9k.Tests", "Fixtures", "PromptGoldens",
            "ReviewLapPromptBuilder", $"{name}.golden.txt");

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find this checkout's own Hall9k.slnx above " + AppContext.BaseDirectory);
    }

    private static ReviewLapBriefing FullBriefing() => new(
        TaskId,
        PullRequest(),
        "hall9k",
        "/home/reviewer/.hall9k/projects/hall9k/repo",
        "/home/reviewer/.hall9k/projects/hall9k/repo/wt-pr-42",
        StatedObjective: "Teach the closeout monitor to read a rebase conflict",
        AcceptanceCriteria: ["the conflict is observed, never inferred", "the dispute parks for the human"],
        AuthorTaskShortId: "28b19893",
        FindingsReport: "## Adversarial\n\nNo defects found.\n\n## Conformance\n\nMatches the stated objective.",
        AuthorRun: new ReviewLapAuthorRun(
            Settlement: "merge-ready after two cycles",
            ResidualsFixed: 3,
            ResidualsRouted: 1,
            UnclaimedResiduals: ["the retry backoff is still linear, not exponential"],
            Rulings: ["cycle 1: the reviewer accepted the linear backoff as a deliberate simplification"]));

    private static ReviewLapBriefing MinimalBriefing() => new(
        TaskId,
        PullRequest() with { Files = [], ChangedFiles = 0, ChecksObserved = false, Checks = [] },
        "hall9k",
        "/home/reviewer/.hall9k/projects/hall9k/repo",
        WorktreePath: string.Empty,
        StatedObjective: null,
        AcceptanceCriteria: [],
        AuthorTaskShortId: null,
        FindingsReport: null,
        AuthorRun: null);

    private static ReviewLapBriefing SinceMyReviewBriefing() => FullBriefing() with
    {
        SinceMyReview = new ScopedReviewPacket(
            ReviewerLogin: "octocat",
            ReviewedHeadSha: "0f1e2d3c4b5a69788796a5b4c3d2e1f001234567",
            CurrentHeadSha: "aa11bb22cc33dd44ee55ff660011223344556677",
            Threads:
            [
                new ScopedReviewThreadDelta(
                    "src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:88",
                    IsResolved: false,
                    NewComments: ["The retry loop still doubles from the session-error backoff; is that deliberate?"],
                    UnreadCommentCount: 2),
                new ScopedReviewThreadDelta(
                    "tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs:12",
                    IsResolved: true,
                    NewComments: []),
            ],
            UnchangedThreadCount: 1,
            NewCommits: ["a1b2c3d Fold the review fix into its owning commit"],
            Diff: "diff --git a/src/Hall9k.Daemon/Closeout/CloseoutEngine.cs b/src/Hall9k.Daemon/Closeout/CloseoutEngine.cs\n+// widened the retry window",
            DiffNote: null,
            ThreadPageTruncated: true,
            ReReviewRequested: true),
    };

    private static ReviewLapBriefing SinceMyReviewMinimalBriefing() => MinimalBriefing() with
    {
        SinceMyReview = new ScopedReviewPacket(
            ReviewerLogin: "octocat",
            ReviewedHeadSha: null,
            CurrentHeadSha: null,
            Threads: [],
            UnchangedThreadCount: 0,
            NewCommits: [],
            Diff: null,
            DiffNote: "The reviewed commit could no longer be found; the branch may have been force-pushed.",
            ThreadPageTruncated: false,
            ReReviewRequested: false),
    };

    private static PullRequestSurface PullRequest() => new(
        "acme/web",
        42,
        "Teach the closeout monitor to read a rebase conflict",
        "Adds the conflict read and the dispute park behind it.",
        "OPEN",
        "main",
        "task/9f2-conflict-read",
        "0f1e2d3c4b5a69788796a5b4c3d2e1f001234567",
        new Uri("https://github.com/acme/web/pull/42"),
        "someone-else",
        Additions: 120,
        Deletions: 18,
        ChangedFiles: 2,
        Files:
        [
            new PullRequestFileChange("src/Hall9k.Daemon/Closeout/CloseoutEngine.cs", 80, 10),
            new PullRequestFileChange("tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs", 40, 8),
        ],
        Checks:
        [
            new PullRequestCheck("build", "ci", "COMPLETED", "SUCCESS"),
            new PullRequestCheck("test", "ci", "IN_PROGRESS", null),
        ],
        ChecksObserved: true);
}
