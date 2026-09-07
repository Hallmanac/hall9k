using FluentAssertions;
using Hall9k.Daemon.Execution;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The base a stacked child's pull request actually opens against, when its parent's branch is
/// already gone from origin (adversarial review, cycle 6). This is the stacked edge's mainline
/// race: the child dispatches at the parent's Delivered, so the parent's merge — and the branch
/// deletion its closeout performs unconditionally — routinely lands during the child's own
/// hours-long build and review pipeline. <c>gh pr create --base &lt;deleted branch&gt;</c> is a raw
/// 422 that failed the run and the task, and the retry inherited the same frozen base and failed
/// identically, forever: every retarget and replay mechanism this feature has is reachable only
/// from closeout's inspection of an already-open pull request.
/// <para>
/// The decision is asserted here rather than the whole open path, deliberately: reaching
/// <c>gh pr create</c> at all needs a real GitHub origin, while what regresses is the direction
/// this maps an unreadable remote in. A remote git could not READ is not a branch that is gone
/// (AGENTS.md's never-guess rule), and guessing a live parent's branch away would open a stacked
/// pull request against the wrong base — the same defect in the opposite direction.
/// </para>
/// </summary>
public sealed class StackedPullRequestOpenBaseTests
{
    private const string ParentBranch = "task/parent-slice-one";

    /// <summary>Exit 2 is git's own documented "no matching ref" from <c>ls-remote --exit-code</c>.</summary>
    [Fact]
    public void A_parent_branch_missing_from_origin_moves_the_pull_request_onto_the_project_base()
        => PullRequestOpener.OpenBaseFor(ParentBranch, "main", lsRemoteExitCode: 2)
            .Should().Be("main",
                "the parent merged mid-build and its closeout deleted the branch, so opening against it is a "
                + "422 — and the project's base is exactly where the retarget would have put this pull request");

    [Fact]
    public void A_parent_branch_still_on_origin_keeps_the_recorded_base()
        => PullRequestOpener.OpenBaseFor(ParentBranch, "main", lsRemoteExitCode: 0)
            .Should().Be(ParentBranch, "which is what forms the stack on GitHub");

    /// <summary>
    /// Every other exit code is git failing to answer — an unreachable origin, a credential prompt,
    /// a repository it cannot read — which is not the same fact as a missing branch. The recorded
    /// base stands, gh fails the run honestly, and h9k task retry asks again.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public void An_unreadable_origin_is_not_a_missing_branch(int exitCode)
        => PullRequestOpener.OpenBaseFor(ParentBranch, "main", exitCode)
            .Should().Be(ParentBranch,
                "a failed read is not evidence the parent's branch is gone, and retargeting on a guess would "
                + "aim a stacked pull request at the wrong base while the parent is still open");

    /// <summary>
    /// The fallback's own warning renders. Asserted because the fallback runs INSIDE the log
    /// call's blast radius: the template repeats {ParentBranch} and {BaseBranch}, MEL binds
    /// placeholders positionally per occurrence without deduplicating names, and passing one
    /// argument per NAME rather than per OCCURRENCE threw a FormatException out of LogWarning
    /// itself — before ResolveOpenBaseAsync could return the base the decision above had just
    /// picked. The run then failed on "An error occurred while writing to logger(s)", and every
    /// retry resumed the branch and failed identically, which is the permanent-failure loop this
    /// whole path exists to end (independent pre-PR review, cycle 8, both lenses).
    /// <para>
    /// Rendered through a logger that actually calls the formatter, because that is where the
    /// binding happens: the integration tests' NullLogger never formats, so nothing else here
    /// would notice.
    /// </para>
    /// </summary>
    [Fact]
    public void The_fallbacks_own_warning_renders_every_placeholder_it_names()
    {
        RenderingLogger logger = new();

        PullRequestOpener.LogStackedParentBranchGone(logger, Guid.Empty, ParentBranch, "main");

        logger.Messages.Should().ContainSingle().Which.Should().Be(
            $"Run {Guid.Empty}: the stacked parent branch {ParentBranch} is not on origin — merged and deleted "
            + "while this branch was still building, or never pushed at all — so this pull request opens against "
            + $"main instead. The run still records {ParentBranch} as its base, because this branch still carries "
            + "the parent's commits and closeout's replay onto main is still owed",
            "each repeated placeholder needs its argument repeated too, and in its own position — a message "
            + "that names the parent branch where it means the project's base would misreport the retarget");

        logger.Messages.Should().ContainSingle().Which.Should().Contain("or never pushed at all",
            "ls-remote's missing-ref code is returned just as readily by a parent branch that was never "
            + "pushed, so a merge asserted from it alone is provenance nothing observed");
    }

    /// <summary>
    /// An <see cref="ILogger"/> that renders rather than discards. <c>NullLogger</c> and the test
    /// hosts' own providers can skip the formatter entirely, and a template whose arguments do not
    /// match its placeholders only fails when something formats it.
    /// </summary>
    private sealed class RenderingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
