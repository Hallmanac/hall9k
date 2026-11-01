using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The rule that keeps a checkout from moving under a session reading it: the first reader refreshes, a reader that joins
/// while another holds a lease leaves the tree where it stands and is told so, and the refresh runs again once the lease
/// is released. The paths are never touched on disk.
/// </summary>
public sealed class ReadingCheckoutLeasesTests
{
    private const string Checkout = "/h/demo/repo/dev";

    private static Func<Task<CheckoutRefresh>> Refresh(Counter counter) =>
        () =>
        {
            counter.Value++;
            return Task.FromResult(new CheckoutRefresh(UpToDate: true, "fast-forwarded 2 commit(s) to origin/main"));
        };

    private sealed class Counter
    {
        public int Value { get; set; }
    }

    [Fact]
    public async Task A_second_reader_leaves_the_checkout_where_it_stands_and_is_told_so_until_the_first_has_ended()
    {
        ReadingCheckoutLeases leases = new();
        Counter refreshes = new();

        ReadingCheckoutJoin first = await leases.JoinAsync(Checkout, Refresh(refreshes), CancellationToken.None);
        ReadingCheckoutJoin second = await leases.JoinAsync(Checkout, Refresh(refreshes), CancellationToken.None);

        first.Refresh.Should().NotBeNull().And.Match<CheckoutRefresh>(refresh => refresh.UpToDate);
        second.Refresh.Should().NotBeNull().And.Match<CheckoutRefresh>(refresh => !refresh.UpToDate && refresh.Detail.Contains("another session is reading it"));
        refreshes.Value.Should().Be(1, "the tree is moved once, before anyone is reading it");

        first.Lease.Dispose();
        ReadingCheckoutJoin third = await leases.JoinAsync(Checkout, Refresh(refreshes), CancellationToken.None);
        third.Refresh!.UpToDate.Should().BeFalse("the second reader still holds its lease");

        second.Lease.Dispose();
        third.Lease.Dispose();
        third.Lease.Dispose();
        ReadingCheckoutJoin later = await leases.JoinAsync(Checkout, Refresh(refreshes), CancellationToken.None);
        later.Refresh!.UpToDate.Should().BeTrue("every lease has been released, and releasing twice counts once");
        refreshes.Value.Should().Be(2);
    }

    [Fact]
    public async Task A_checkout_the_daemon_does_not_move_is_never_refreshed_and_other_checkouts_are_independent()
    {
        ReadingCheckoutLeases leases = new();
        Counter refreshes = new();

        ReadingCheckoutJoin untouched = await leases.JoinAsync(Checkout, null, CancellationToken.None);
        ReadingCheckoutJoin elsewhere = await leases.JoinAsync("/h/other/repo/dev", Refresh(refreshes), CancellationToken.None);

        untouched.Refresh.Should().BeNull();
        elsewhere.Refresh!.UpToDate.Should().BeTrue("a different checkout has no reader");

        // The one that was never refreshed still counts as a reader, so a later session does not move it either.
        ReadingCheckoutJoin joined = await leases.JoinAsync(Checkout, Refresh(refreshes), CancellationToken.None);
        joined.Refresh!.UpToDate.Should().BeFalse();
        refreshes.Value.Should().Be(1);
    }
}
