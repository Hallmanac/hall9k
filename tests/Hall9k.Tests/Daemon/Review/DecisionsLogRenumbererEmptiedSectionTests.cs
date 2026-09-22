using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon.Review;

/// <summary>
/// What <see cref="DecisionsLogRenumberer"/> does once PLAN.md's §16 carries no numbered entry at
/// all, which is every base from idea d805fd8b piece 3 onward: the log is the decision store now
/// and the heading is a pointer at the rendered <c>decisions.md</c>. The step still ships, so an
/// in-flight branch that appended a placeholder before that landed still reaches it on its own
/// rebase, and the number it would otherwise mint is <c>MaxRealNumber + 1</c> == #1 — a citation
/// the import already handed to the entry that used to stand at §16 #1, so every citation of the
/// branch's own placeholder would be rewritten to point at somebody else's decision (independent
/// pre-PR review, cycle 1, conformance lens).
/// <para>
/// No repository and no git: the decline happens before the first git call, which is exactly what
/// a <see cref="ProcessRunner"/> that throws on any invocation proves, and PLAN.md is a file in a
/// temporary directory rather than a checkout (the testing rule, Brian 2026-09-13). The step's own
/// happy paths keep their real-git fixtures in
/// <see cref="DecisionsLogRenumbererFixtureTests"/> and <see cref="DecisionsLogRenumbererTransitionTests"/>,
/// because there the git calls are the thing under test.
/// </para>
/// </summary>
public sealed class DecisionsLogRenumbererEmptiedSectionTests : IDisposable
{
    private readonly string _worktree = Directory.CreateTempSubdirectory("h9k-renumber-empty-").FullName;

    public void Dispose() => Directory.Delete(_worktree, recursive: true);

    private static readonly ProcessRunner NeverRuns =
        (fileName, arguments, _, _) => throw new InvalidOperationException(
            $"the renumberer reached the operating system ('{fileName} {string.Join(' ', arguments)}') on a "
            + "section it has no number space to read, so it had already decided to rewrite something");

    [Fact]
    public async Task A_section_with_no_numbered_entry_left_declines_rather_than_minting_the_first_number()
    {
        string planPath = Path.Combine(_worktree, "PLAN.md");
        string plan = string.Join('\n',
        [
            "## 16. v0 Decisions Log",
            "",
            "The log this section carried is platform data now (idea d805fd8b). Every decision is an",
            "event in the store, and the file to read is `decisions.md`.",
            "",
            "PLACEHOLDER-c570bb16. **An in-flight branch's own entry.** Appended before the section was",
            "emptied, and still sitting at its tail after this branch rebased onto the base that emptied it.",
            "",
            "---",
            "",
            "## 17. Reference Materials",
            "",
        ]);
        await File.WriteAllTextAsync(planPath, plan, CancellationToken.None);

        DecisionsLogRenumberResult result = await DecisionsLogRenumberer.RenumberIfNeededAsync(
            NeverRuns, _worktree, "0000000000000000000000000000000000000000",
            "1111111111111111111111111111111111111111", "c570bb16", CancellationToken.None);

        result.Outcome.Should().Be(DecisionsLogRenumberOutcome.NoActionNeeded,
            "#1 belongs to the imported entry that used to stand at §16 #1, so assigning it here would "
            + "point this branch's own citations at somebody else's decision");
        result.NewNumber.Should().BeNull();
        result.OldToken.Should().BeNull();
        result.FilesRewritten.Should().Be(0);

        string after = await File.ReadAllTextAsync(planPath, CancellationToken.None);
        after.Should().Be(plan,
            "declining means the placeholder is left exactly as the branch wrote it, for the mandatory "
            + "gate's own numbering guard to fail by name");
    }
}
