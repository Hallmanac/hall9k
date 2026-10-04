using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Two source scans that keep the assign/queue split from decaying: the set of places that write the go
/// event (<c>TaskAssigned</c>) stays the set this card named, and no line in <c>src/</c> that still
/// mentions <c>h9k task assign</c> or <c>--assign</c> calls it the go signal again.
/// </summary>
public sealed partial class AssignQueueSplitGuardTests
{
    /// <summary>
    /// Every caller of <c>TaskDecider.Assign</c> in <c>src/</c>, by file and count. <c>h9k task queue</c> and
    /// <c>h9k task publish --queue</c> reach it through the one call in <c>TaskAssignCommand.AppendAsync</c>;
    /// start, work, pr review and the two auto-pr-review mints queue in their own append because each is a go
    /// act of its own, not an assignment. A new caller is a new way to dispatch a task, so it has to be
    /// named here on purpose.
    /// </summary>
    private static readonly (string File, int Calls)[] GoEventCallers =
    [
        (Path.Combine("Hall9k.Cli", "Commands", "TaskAssignCommand.cs"), 1),
        (Path.Combine("Hall9k.Cli", "Commands", "TaskStartCommand.cs"), 1),
        (Path.Combine("Hall9k.Cli", "Commands", "TaskWorkCommand.cs"), 1),
        (Path.Combine("Hall9k.Cli", "Commands", "PullRequestReviewCommand.cs"), 2),
        (Path.Combine("Hall9k.Daemon", "AutoPrReview", "AutoPrReviewEngine.cs"), 2),
    ];

    [Fact]
    public void Only_the_named_go_acts_write_TaskAssigned()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();
        Dictionary<string, int> found = [];
        foreach (string file in SourceFiles(sourceDirectory))
        {
            (string code, _, _) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));
            int calls = TaskDeciderAssignCall().Matches(code).Count;
            if (calls > 0)
            {
                found[Path.GetRelativePath(sourceDirectory, file)] = calls;
            }
        }

        found.Should().BeEquivalentTo(
            GoEventCallers.ToDictionary(caller => caller.File, caller => caller.Calls),
            "these are the go acts: queue (through AppendAsync), start, work, pr review and both auto-pr-review mints");

        List<string> constructors = [];
        foreach (string file in SourceFiles(sourceDirectory))
        {
            (string code, _, _) = TestSourceTree.StripCommentsAndStrings(File.ReadAllText(file));
            if (NewTaskAssigned().IsMatch(code))
            {
                constructors.Add(Path.GetRelativePath(sourceDirectory, file));
            }
        }

        constructors.Should().Equal(
            [Path.Combine("Hall9k.Domain", "Features", "Tasks", "Handlers", "TaskDecider.cs")],
            "the decider builds the one TaskAssigned; nothing else may construct the go event");
    }

    /// <summary>
    /// Words that say a line is calling something the way to start a task. The one place a line naming
    /// assign may still say them is the wire event's own doc comment, which explains the naming trap.
    /// </summary>
    private static readonly string[] GoWords =
        ["go signal", "the human go", "dispatch trigger", "so it dispatches", "starts it", "so it can dispatch"];

    [Fact]
    public void No_line_that_names_task_assign_or_the_assign_flag_calls_it_the_go()
    {
        string sourceDirectory = TestSourceTree.SourceDirectory();
        string wireEvent = Path.Combine("Hall9k.Domain", "Features", "Tasks", "Events", "TaskAssigned.cs");

        List<string> offenders = [];
        foreach (string file in SourceFiles(sourceDirectory))
        {
            string relative = Path.GetRelativePath(sourceDirectory, file);
            if (relative == wireEvent)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                bool namesAssign = line.Contains("task assign", StringComparison.Ordinal)
                    || line.Contains("--assign", StringComparison.Ordinal);
                if (namesAssign && GoWords.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{relative}:{index + 1}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "assign records who holds a task and never dispatches it; h9k task queue is the go signal");
    }

    private static IEnumerable<string> SourceFiles(string sourceDirectory) =>
        Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !TestSourceTree.IsBuildOutput(sourceDirectory, file));

    [GeneratedRegex(@"TaskDecider\s*\.\s*Assign\s*\(")]
    private static partial Regex TaskDeciderAssignCall();

    [GeneratedRegex(@"new\s+TaskAssigned\s*\(")]
    private static partial Regex NewTaskAssigned();
}
