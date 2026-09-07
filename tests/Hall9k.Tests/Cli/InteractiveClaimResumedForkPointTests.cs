using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What the CLI's two interactive claim doors record as a resumed branch's fork point
/// (conformance review, cycle 8). <c>RunLauncher</c> checks a resumed
/// <c>RunDispatched.BaseCommit</c> against the branch before re-asserting it; these doors did not,
/// and <see cref="StackedBaseResolver.ResumedBaseAsync"/>'s own doc is explicit that all three
/// doors have to read a resumed branch identically or one of them records a fork point
/// contradicting what the branch holds.
/// <para>
/// The commit at stake is a stacked replay's dispatch-time PREDICTION — the commit the replay was
/// told to land on, recorded before the session rebases anything — and the replay's prompt
/// sanctions <c>git rebase --abort</c> on a conflict it cannot honestly resolve. Carrying that
/// forward hands the interactive session a recompose boundary and self-review ranges computed from
/// a commit the branch never reached, folding the parent's whole delta into what the session
/// treats as its own work.
/// </para>
/// </summary>
public sealed class InteractiveClaimResumedForkPointTests
{
    private const string ParentBranch = "task/parent-slice-one";

    private const string ChildBranch = "task/child-slice-two";

    private const string RecordedForkPoint = "9999888877776666555544443333222211110000";

    private const string FreshStartPoint = "1111222233334444555566667777888899990000";

    [Fact]
    public async Task A_resumed_stacked_branch_that_never_landed_on_the_recorded_fork_point_records_none()
    {
        (string forkPoint, List<IReadOnlyList<string>> calls) = await ResumeAsync(ContainmentRunner(exitCode: 1));

        forkPoint.Should().BeEmpty(
            "blank is the honest record — nothing else names where this branch actually forked from, and the "
            + "stacked prompts dispute an unobserved boundary rather than replaying from a guess");
        calls.Should().ContainSingle().Which.Should().Equal(
            ["merge-base", "--is-ancestor", RecordedForkPoint, $"refs/heads/{ChildBranch}"]);
    }

    [Fact]
    public async Task A_resumed_stacked_branch_that_holds_the_recorded_fork_point_carries_it_forward()
    {
        (string forkPoint, _) = await ResumeAsync(ContainmentRunner(exitCode: 0));

        forkPoint.Should().Be(RecordedForkPoint,
            "a fork point an earlier run observed cannot be re-observed — a resumed checkout performs no fresh "
            + "cut — so discarding it would leave the retarget and replay permanently unobservable");
    }

    /// <summary>
    /// Git's own three-way reading of <c>--is-ancestor</c>: 0 contained, 1 not, anything else a
    /// failure to answer. A failure is not evidence, and blanking on one would cost this branch its
    /// only boundary permanently (AGENTS.md's never-guess rule).
    /// </summary>
    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    public async Task Git_failing_to_answer_leaves_the_recorded_fork_point_alone(int exitCode)
    {
        (string forkPoint, _) = await ResumeAsync(ContainmentRunner(exitCode));

        forkPoint.Should().Be(RecordedForkPoint,
            "an unreadable repository is not the same fact as a branch missing a commit");
    }

    [Fact]
    public async Task A_thrown_git_call_leaves_the_recorded_fork_point_alone()
    {
        (string forkPoint, _) = await ResumeAsync(
            (_, _, _, _) => throw new TimeoutException("git never answered"));

        forkPoint.Should().Be(RecordedForkPoint, "for the same reason a nonzero exit does");
    }

    [Fact]
    public async Task A_fresh_cut_records_its_own_start_point_without_asking_git_anything()
    {
        List<IReadOnlyList<string>> calls = [];
        string forkPoint = await TaskWorkCommand.ResumedForkPointAsync(
            Recording(ContainmentRunner(exitCode: 1), calls),
            resumedBase: null,
            new Worktree("/tmp/child-wt", ChildBranch, ParentBranch, FreshStartPoint),
            ParentBranch,
            Project(),
            CancellationToken.None);

        forkPoint.Should().Be(FreshStartPoint, "the cut just observed its own start point");
        calls.Should().BeEmpty("there is nothing inherited here to check against the branch");
    }

    /// <summary>
    /// An unstacked run pays nothing for this: blank-versus-recorded changes nothing it reads
    /// (<c>RunDetails.StackedForkPoint</c> answers null once the base is the project's own), so the
    /// git call is not made at all.
    /// </summary>
    [Fact]
    public async Task An_unstacked_resume_asks_git_nothing()
    {
        List<IReadOnlyList<string>> calls = [];
        string forkPoint = await TaskWorkCommand.ResumedForkPointAsync(
            Recording(ContainmentRunner(exitCode: 1), calls),
            new StackedBaseResolver.ResumedBase("main", RecordedForkPoint),
            new Worktree("/tmp/child-wt", ChildBranch, "main"),
            "main",
            Project(),
            CancellationToken.None);

        forkPoint.Should().Be(RecordedForkPoint);
        calls.Should().BeEmpty("nothing an unstacked run reads distinguishes the two answers");
    }

    private static async Task<(string ForkPoint, List<IReadOnlyList<string>> Calls)> ResumeAsync(ProcessRunner git)
    {
        List<IReadOnlyList<string>> calls = [];
        string forkPoint = await TaskWorkCommand.ResumedForkPointAsync(
            Recording(git, calls),
            new StackedBaseResolver.ResumedBase(ParentBranch, RecordedForkPoint),
            // A resumed checkout performs no fresh cut, so it reports no start point of its own —
            // which is exactly why the inherited value is the only candidate here.
            new Worktree("/tmp/child-wt", ChildBranch, ParentBranch),
            ParentBranch,
            Project(),
            CancellationToken.None);
        return (forkPoint, calls);
    }

    private static ProjectDetails Project() => new() { BaseBranch = "main", RepositoryPath = "/tmp/repo" };

    private static ProcessRunner ContainmentRunner(int exitCode) =>
        (_, _, _, _) => Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty));

    private static ProcessRunner Recording(ProcessRunner inner, List<IReadOnlyList<string>> calls) =>
        (fileName, arguments, workingDirectory, cancellationToken) =>
        {
            fileName.Should().Be("git");
            calls.Add(arguments);
            return inner(fileName, arguments, workingDirectory, cancellationToken);
        };
}
