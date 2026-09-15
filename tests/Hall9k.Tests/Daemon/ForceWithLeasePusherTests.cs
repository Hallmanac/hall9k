using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The ancestor-or-reflog-or-recorded force-with-lease guard, exercised entirely over a fake git
/// (a scripted <see cref="ProcessRunner"/>, never a real repository) rather than the Docker-backed
/// integration tests in <c>PullRequestOpenerTests</c>: this suite's whole point is the guard's own
/// decision logic, which does not need a real worktree to prove.
/// </summary>
public sealed class ForceWithLeasePusherTests
{
    private const string Branch = "task/62acc347-shape";
    private const string RecordedTip = "59f084f5aa11bb22cc33dd44ee55ff6677889900";

    /// <summary>
    /// A fake git that answers <c>ls-remote</c>, <c>merge-base --is-ancestor</c>, <c>reflog show</c>
    /// and <c>push</c> from a script, recording every call it received so a test can assert on the
    /// exact lease pinned.
    /// </summary>
    private sealed class FakeGit
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public string OriginTip { get; init; } = RecordedTip;
        public bool OriginTipIsAncestor { get; init; }
        public IReadOnlyList<string> ReflogEntries { get; init; } = [];

        public ProcessRunner Runner => (fileName, arguments, workingDirectory, _) =>
        {
            Calls.Add(arguments);
            fileName.Should().Be("git");
            return Task.FromResult(arguments[0] switch
            {
                "ls-remote" => new ProcessResult(0, $"{OriginTip}\trefs/heads/{Branch}\n", string.Empty),
                "merge-base" => new ProcessResult(OriginTipIsAncestor ? 0 : 1, string.Empty, string.Empty),
                "reflog" => new ProcessResult(0, string.Join('\n', ReflogEntries), string.Empty),
                "push" => new ProcessResult(0, string.Empty, string.Empty),
                _ => throw new InvalidOperationException($"unexpected git subcommand: {arguments[0]}"),
            });
        };
    }

    /// <summary>
    /// The 2026-09-15 shape itself (task 62acc347 run 01a0a376, and task 6189c968 run 01a0a3a8):
    /// a follow-up lap's autosquash rewrote the commit this node previously pushed onto a new local
    /// history that does not carry it as an ancestor, and a sibling session's <c>git filter-branch</c>
    /// plus <c>git reflog expire --all</c> in the same shared repository wiped this branch's reflog
    /// along with the one it meant to rewrite. Neither the ancestor check nor the reflog check can
    /// tell this node's own prior push from a foreign one anymore — only the durable record can, and
    /// this proves the guard reads it and pins the lease to the recorded tip rather than failing the
    /// run.
    /// </summary>
    [Fact]
    public async Task A_recorded_prior_push_survives_an_expired_reflog_and_a_rewritten_local_history()
    {
        FakeGit git = new() { OriginTipIsAncestor = false, ReflogEntries = [] };

        await ForceWithLeasePusher.PushAsync(
            git.Runner, "/worktree", Branch, new HashSet<string> { RecordedTip }, CancellationToken.None);

        IReadOnlyList<string> push = git.Calls.Should().ContainSingle(call => call[0] == "push").Subject;
        push.Should().Contain($"--force-with-lease={Branch}:{RecordedTip}",
            "the lease must pin to the recorded tip the guard just verified, not a bare flag");
    }

    /// <summary>
    /// The refusal branch that is this guard's whole protective value: a tip that is neither an
    /// ancestor of local HEAD, nor in the branch's own reflog, nor ever recorded as a tip this node
    /// pushed must still be refused — someone else moved the branch, and the recorded-tip door added
    /// for the 2026-09-15 incident must not turn into a door that accepts anything.
    /// </summary>
    [Fact]
    public async Task A_tip_this_node_never_pushed_is_still_refused()
    {
        FakeGit git = new() { OriginTip = "deadbeef00112233445566778899aabbccddeeff", OriginTipIsAncestor = false, ReflogEntries = [] };

        Func<Task> push = () => ForceWithLeasePusher.PushAsync(
            git.Runner, "/worktree", Branch, new HashSet<string> { RecordedTip }, CancellationToken.None);

        (await push.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*a tip this node's own history never held*");
        git.Calls.Should().NotContain(call => call[0] == "push",
            "a refused push must never reach the actual push call");
    }

    /// <summary>
    /// The ordinary fast-forward case still needs no recorded tip at all: an empty recorded set must
    /// not regress the ancestor door the guard already had.
    /// </summary>
    [Fact]
    public async Task An_ancestor_tip_needs_no_recorded_tip_at_all()
    {
        FakeGit git = new() { OriginTipIsAncestor = true };

        await ForceWithLeasePusher.PushAsync(
            git.Runner, "/worktree", Branch, new HashSet<string>(), CancellationToken.None);

        git.Calls.Should().ContainSingle(call => call[0] == "push");
        git.Calls.Should().NotContain(call => call[0] == "reflog",
            "the ancestor check alone is enough; the guard should not need the reflog fallback");
    }
}
