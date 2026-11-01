using Hall9k.Connectors.Worktrees;

namespace Hall9k.Daemon.Execution;

/// <summary>What joining a reading checkout came to: the refresh the join ran (or declined to), and the lease to release when the session ends.</summary>
public sealed record ReadingCheckoutJoin(CheckoutRefresh? Refresh, IDisposable Lease);

/// <summary>
/// Who is reading a project's checkout right now, so that a draft challenge or a plan's refinement session never brings it up
/// to the remote base branch under another of the two that was told to test against one commit. Other refreshes of the same
/// checkout (a run's gate comparison, <c>h9k task verify</c>, <c>h9k project set</c>) take no lease, so this is a guarantee between
/// these two kinds of session and not against every writer. A draft challenge and a plan's refinement session both read the
/// project home's own <c>dev/</c> and both refresh it before they start, and they run on loops of their own, so the serial
/// sweep that kept a challenge's refresh from reaching another challenge's session does not reach across the two.
/// <para>
/// A session joins before it refreshes and holds its lease until it has ended. The first reader refreshes; one that joins
/// while another holds a lease leaves the tree where it is and is told so, in the same words a failed refresh uses, so its
/// prompt says the checkout may be behind. The refresh and the join are one step under a gate, so two sessions starting at
/// once cannot both refresh and then both read. Held in memory on purpose: a session cannot outlive the daemon that
/// dispatched it (the next start stops it), so a restart leaves no lease behind.
/// </para>
/// </summary>
public sealed class ReadingCheckoutLeases
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _readers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Joins the checkout as a reader. <paramref name="refresh"/> is null for a checkout the daemon does not move (the
    /// answer is then null too); otherwise it runs only when nobody else is reading.
    /// </summary>
    public async Task<ReadingCheckoutJoin> JoinAsync(
        string checkout, Func<Task<CheckoutRefresh>>? refresh, CancellationToken cancellationToken)
    {
        string key = Path.GetFullPath(checkout);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            bool held;
            lock (_readers)
            {
                held = _readers.TryGetValue(key, out int count) && count > 0;
            }

            CheckoutRefresh? outcome = refresh is null
                ? null
                : held
                    ? new CheckoutRefresh(
                        UpToDate: false,
                        "it was left where it stood because another session is reading it, and moving it would change what that session tests against")
                    : await refresh();

            lock (_readers)
            {
                _readers[key] = _readers.GetValueOrDefault(key) + 1;
            }

            return new ReadingCheckoutJoin(outcome, new Lease(this, key));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Release(string key)
    {
        lock (_readers)
        {
            int remaining = _readers.GetValueOrDefault(key) - 1;
            if (remaining > 0)
            {
                _readers[key] = remaining;
            }
            else
            {
                _readers.Remove(key);
            }
        }
    }

    private sealed class Lease(ReadingCheckoutLeases owner, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(key);
            }
        }
    }
}
