using FluentAssertions;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Byte-for-byte proof of what the Security persona's own review is handed. The persona's prompt
/// had no content test before the pre-PR Adversarial lens began reading the same six areas out of
/// the same <c>lenses.md</c>; this fixture is what keeps an edit to the shared reading code from
/// quietly changing the pr-review persona.
/// </summary>
// Redirects HALL9K_HOME to an empty temp home, the same as the other golden suites and for the
// identical reason: PromptTemplates falls back to a HALL9K_HOME-derived canonical directory
// whenever this checkout's own .claude/templates does not carry a file it asks for.
public sealed class SecurityReviewPromptBuilderGoldenTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();

    private static readonly Guid TaskId = Guid.Parse("01a09285-6a21-7259-a883-c5ebe5482c3e");
    private static readonly Guid RunId = Guid.Parse("01a09285-7b32-7259-a883-c5ebe5482c3e");

    public void Dispose()
    {
        _scopedHome.Dispose();
    }

    [Fact]
    public void The_security_personas_own_prompt_matches_its_golden()
    {
        TaskDetails task = new()
        {
            Id = TaskId,
            Type = TaskType.PrReview,
            Objective = "Review pull request #42",
            AcceptanceCriteria = ["Every finding is verified before it is reported"],
            CurrentRunId = RunId,
        };
        ProjectDetails project = new() { Name = "hall9k", BaseBranch = "main" };

        string prompt = SecurityReviewPromptBuilder.Build(
            new ReviewPersonaPromptRequest(task, project, "detached", "develop", TimeSpan.FromMinutes(20)));

        AssertMatchesGolden("security-review-persona", prompt);
    }

    private static void AssertMatchesGolden(string name, string actual)
    {
        actual.ShouldHaveOnlyLineFeeds();
        string path = GoldenPath(name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Normalize(actual));
            return;
        }

        File.Exists(path).Should().BeTrue($"golden fixture {path} must be captured before this test can run");
        Normalize(actual).Should().Be(Normalize(File.ReadAllText(path)));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string GoldenPath(string name) =>
        Path.Combine(RepositoryRoot(), "tests", "Hall9k.Tests", "Fixtures", "PromptGoldens",
            "SecurityReviewPromptBuilder", $"{name}.golden.txt");

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
}
